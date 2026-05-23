using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Client.Core.Network;

/// <summary>
/// Client-side WebSocket client for signaling server communication.
/// Supports both text (JSON signaling) and binary (transport relay) messages.
/// </summary>
public sealed class SignalingClient : IAsyncDisposable
{
    private readonly ILogger<SignalingClient> _logger;
    private readonly string _serverUrl;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private PeriodicTimer? _pingTimer;
    private Task? _pingTask;
    private Channel<SignalingMessage> _incomingMessages;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // Stored credentials for ReconnectAsync. Set on successful RegisterAsync, cleared
    // on DisconnectAsync. Without these, ReconnectAsync has no identity to re-register
    // with, so it short-circuits.
    private string? _lastClientId;
    private string? _lastPasswordHash;
    // Guards against concurrent ReconnectAsync invocations (receive-loop fires one and
    // a subscriber might fire another). WaitAsync(0) short-circuits the second caller.
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    // Gates whether receive-loop-triggered reconnect runs at all. Set true on first
    // successful RegisterAsync, cleared on DisconnectAsync so a deliberate close does
    // not immediately trigger a reconnect loop.
    private bool _reconnectEnabled;

    /// <summary>
    /// Event raised when a text (JSON signaling) message is received from the server.
    /// </summary>
    public event Action<SignalingMessage>? OnMessageReceived;

    /// <summary>
    /// Event raised when a binary message is received (transport relay data).
    /// Parameters: (byte[] data, int length)
    /// </summary>
    public event Action<byte[], int>? OnBinaryReceived;

    /// <summary>
    /// Event raised when the connection is closed.
    /// </summary>
    public event Action<string?>? OnDisconnected;

    /// <summary>
    /// Event raised when an error occurs.
    /// </summary>
    public event Action<Exception>? OnError;

    /// <summary>
    /// Raised after SIG-RECONNECT succeeds (RegisterAsync re-completed on a fresh WS).
    /// HostSessionManager subscribes to send `host_recovered` to its previously-paired viewer
    /// so the viewer can cancel its grace timer (closes TODO §5 P1 "Host-recovered handshake
    /// to survive Railway prune race").
    /// </summary>
    public event Action? OnSignalingReconnected;

    /// <summary>
    /// Current connection state.
    /// </summary>
    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public SignalingClient(string serverUrl, ILogger<SignalingClient> logger)
    {
        _serverUrl = serverUrl;
        _logger = logger;
        _incomingMessages = Channel.CreateUnbounded<SignalingMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
    }

    /// <summary>
    /// Connect to the signaling server.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        // Clean up any existing connection first (allows reconnection)
        if (_webSocket != null)
        {
            _logger.LogInformation("Cleaning up previous connection before reconnecting");
            await DisposeInternalAsync();
        }

        // Recreate the channel for the new connection (previous channel was completed)
        _incomingMessages = Channel.CreateUnbounded<SignalingMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        _webSocket = new ClientWebSocket();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            var uri = new Uri(_serverUrl);
            await _webSocket.ConnectAsync(uri, _cts.Token);
            _logger.LogInformation("Connected to signaling server at {Url}", _serverUrl);

            // Start receive loop
            _receiveTask = ReceiveLoopAsync(_cts.Token);

            // Start keepalive ping timer (prevents Railway proxy from killing idle WS connections)
            _pingTimer = new PeriodicTimer(TimeSpan.FromSeconds(25));
            _pingTask = PingLoopAsync(_pingTimer, _cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to signaling server");
            await DisposeInternalAsync();
            throw;
        }
    }

    /// <summary>
    /// Send a text (JSON signaling) message to the server.
    /// Thread-safe — uses write lock shared with binary sends.
    /// </summary>
    public async Task SendAsync(SignalingMessage message, CancellationToken cancellationToken = default)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Not connected");
        }

        var json = SignalingSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
        finally { _writeLock.Release(); }

        _logger.LogDebug("[SIG] Sent {MessageType}: {Json}", message.GetType().Name,
            System.Text.Json.JsonSerializer.Serialize(message, message.GetType()));
    }

    /// <summary>
    /// Send binary data through the WebSocket (for transport relay).
    /// Thread-safe — uses write lock shared with text sends.
    /// </summary>
    public async Task SendBinaryAsync(byte[] data, int offset, int length, CancellationToken cancellationToken = default)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Not connected");
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _webSocket.SendAsync(
                new ArraySegment<byte>(data, offset, length),
                WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken);
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>
    /// Register with the signaling server.
    /// </summary>
    public async Task<bool> RegisterAsync(string clientId, string passwordHash, CancellationToken cancellationToken = default)
    {
        await SendAsync(new SignalingMessage.Register(clientId, passwordHash), cancellationToken);

        // Wait for response
        var response = await WaitForMessageAsync<SignalingMessage>(
            m => m is SignalingMessage.RegisterSuccess or SignalingMessage.RegisterFailed,
            TimeSpan.FromSeconds(10),
            cancellationToken);

        if (response is SignalingMessage.RegisterSuccess)
        {
            // Store credentials so ReconnectAsync can re-register transparently if the WS dies.
            _lastClientId = clientId;
            _lastPasswordHash = passwordHash;
            _reconnectEnabled = true;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Reconnect to the signaling server and re-register with the stored credentials.
    /// Exponential backoff: 250ms, 500ms, 1s, 2s, 5s, then 10s cap (was 30s). Returns
    /// true on first successful registration, false if cancelled or stored credentials
    /// are missing. Guarded by _reconnectLock so concurrent callers (receive-loop
    /// auto-kick + a subscriber's defensive call) collapse to one in-flight attempt.
    ///
    /// Backoff cap lowered from 30s to 10s on 2026-05-23 after smoke showed the 30s
    /// tail dominating recovery latency for host wifi cycles (~139s SIG-RECONNECT
    /// completion when wifi returned during a 30s sleep). With 10s cap, recovery
    /// drops to ~10s post-wifi-return, which (a) lets the medium-outage scenario
    /// (30-100s wifi off) complete within viewer's 120s max-outage budget, and
    /// (b) makes EPOCH 4 (HostRecovered re-pair) + EPOCH 7-Bug3 (asymmetric UDP
    /// promote) actually reachable instead of always being preempted by max-outage.
    ///
    /// Closes the 2026-05-20 P0 "Signaling WS silent death never repaired": pre-fix,
    /// the receive loop exited on WebSocketException, fired OnDisconnected, and the
    /// session-still-active suppressing handler logged + returned without ever bringing
    /// the WS back up. Railway then pruned the host from its registry, so reconnect
    /// attempts from the viewer returned "Target client is not online."
    /// </summary>
    public async Task<bool> ReconnectAsync(CancellationToken cancellationToken = default)
    {
        if (!_reconnectEnabled || _lastClientId == null || _lastPasswordHash == null)
        {
            _logger.LogDebug("[SIG-RECONNECT] Skipped: enabled={Enabled} hasCredentials={HasCreds}",
                _reconnectEnabled, _lastClientId != null);
            return false;
        }

        if (!await _reconnectLock.WaitAsync(0, cancellationToken))
        {
            _logger.LogDebug("[SIG-RECONNECT] Skipped: another reconnect already in progress");
            return false;
        }

        try
        {
            _logger.LogWarning("[SIG-RECONNECT] Starting reconnect (clientId={ClientId}) with exponential backoff",
                _lastClientId);

            var delaysMs = new[] { 250, 500, 1000, 2000, 5000, 10000 };
            var attempt = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await ConnectAsync(cancellationToken);
                    var ok = await RegisterAsync(_lastClientId, _lastPasswordHash, cancellationToken);
                    if (ok)
                    {
                        _logger.LogInformation("[SIG-RECONNECT] Reconnect succeeded after {Attempt} attempts in {Elapsed}ms",
                            attempt + 1, sw.ElapsedMilliseconds);
                        // Fire event so HostSessionManager can send host_recovered to its previously-paired
                        // viewer (suppresses the viewer's grace-timer-driven RemoveSessionAsync that would
                        // otherwise fire from the Railway stale-WS prune notification).
                        try { OnSignalingReconnected?.Invoke(); }
                        catch (Exception evtEx) { _logger.LogWarning(evtEx, "OnSignalingReconnected subscriber threw"); }
                        return true;
                    }
                    _logger.LogWarning("[SIG-RECONNECT] Re-register failed on attempt {Attempt} (RegisterAsync returned false)",
                        attempt + 1);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("[SIG-RECONNECT] Cancelled mid-attempt after {Attempt} tries", attempt + 1);
                    return false;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[SIG-RECONNECT] Attempt {Attempt} threw", attempt + 1);
                }

                var delayMs = delaysMs[Math.Min(attempt, delaysMs.Length - 1)];
                attempt++;
                _logger.LogDebug("[SIG-RECONNECT] Sleeping {Delay}ms before attempt {Next}", delayMs, attempt + 1);
                try { await Task.Delay(delayMs, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }

            _logger.LogWarning("[SIG-RECONNECT] Cancelled after {Attempt} attempts in {Elapsed}ms",
                attempt, sw.ElapsedMilliseconds);
            return false;
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    /// <summary>
    /// Request connection to a peer. Pre-hashes the password (server never sees plaintext).
    /// </summary>
    public async Task RequestConnectionAsync(string targetId, string password, CancellationToken cancellationToken = default)
    {
        var passwordHash = SteamViewer.Client.Core.Session.PasswordHash.Compute(targetId, password);
        await SendAsync(new SignalingMessage.ConnectRequest(targetId, passwordHash), cancellationToken);
    }

    /// <summary>
    /// Respond to an incoming connection request.
    /// </summary>
    public async Task RespondToConnectionAsync(string targetId, bool approved, CancellationToken cancellationToken = default)
    {
        await SendAsync(new SignalingMessage.ConnectionResponse(targetId, approved), cancellationToken);
    }

    /// <summary>
    /// Send SDP offer to a peer.
    /// </summary>
    public async Task SendSdpOfferAsync(string targetId, string sdp, CancellationToken cancellationToken = default)
    {
        await SendAsync(new SignalingMessage.SdpOffer(targetId, sdp), cancellationToken);
    }

    /// <summary>
    /// Send SDP answer to a peer.
    /// </summary>
    public async Task SendSdpAnswerAsync(string targetId, string sdp, CancellationToken cancellationToken = default)
    {
        await SendAsync(new SignalingMessage.SdpAnswer(targetId, sdp), cancellationToken);
    }

    /// <summary>
    /// Send ICE candidate to a peer.
    /// </summary>
    public async Task SendIceCandidateAsync(string targetId, string candidate, string? sdpMid, ushort? sdpMLineIndex, CancellationToken cancellationToken = default)
    {
        await SendAsync(new SignalingMessage.IceCandidate(targetId, candidate, sdpMid, sdpMLineIndex), cancellationToken);
    }

    /// <summary>
    /// Disconnect from a peer.
    /// </summary>
    public async Task DisconnectFromPeerAsync(string peerId, CancellationToken cancellationToken = default)
    {
        await SendAsync(new SignalingMessage.Disconnect(peerId), cancellationToken);
    }

    /// <summary>
    /// Send a host_recovered notification to a previously-paired viewer. Used by
    /// HostSessionManager after SIG-RECONNECT succeeds; lets the viewer cancel its grace
    /// timer and preserve the existing session instead of running RemoveSessionAsync.
    /// </summary>
    public async Task SendHostRecoveredAsync(string targetPeerId, CancellationToken cancellationToken = default)
    {
        await SendAsync(new SignalingMessage.HostRecovered(targetPeerId), cancellationToken);
        _logger.LogInformation("[HOST-RECOVERED] Sent host_recovered to peer {Peer}", targetPeerId);
    }

    /// <summary>
    /// Send a ping to keep the connection alive.
    /// </summary>
    public async Task PingAsync(CancellationToken cancellationToken = default)
    {
        await SendAsync(new SignalingMessage.Ping(), cancellationToken);
    }

    /// <summary>
    /// Wait for a specific message type.
    /// </summary>
    public async Task<T> WaitForMessageAsync<T>(Func<T, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken = default) where T : SignalingMessage
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        await foreach (var message in _incomingMessages.Reader.ReadAllAsync(linkedCts.Token))
        {
            if (message is T typed && predicate(typed))
            {
                return typed;
            }

            // Re-queue non-matching messages by raising the event
            OnMessageReceived?.Invoke(message);
        }

        throw new TimeoutException($"Timeout waiting for message of type {typeof(T).Name}");
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[65536]; // 64KB for binary relay frames
        var messageBuilder = new StringBuilder();
        var binaryBuffer = new MemoryStream();

        try
        {
            while (_webSocket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    OnDisconnected?.Invoke(result.CloseStatusDescription);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // Accumulate binary chunks
                    binaryBuffer.Write(buffer, 0, result.Count);

                    if (result.EndOfMessage)
                    {
                        var data = binaryBuffer.GetBuffer();
                        var length = (int)binaryBuffer.Length;
                        OnBinaryReceived?.Invoke(data, length);
                        binaryBuffer.SetLength(0); // Reset for next message
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    if (result.EndOfMessage)
                    {
                        var json = messageBuilder.ToString();
                        messageBuilder.Clear();

                        var message = SignalingSerializer.Deserialize(json);
                        if (message != null)
                        {
                            _logger.LogDebug("[SIG] Received {MessageType}: {Json}", message.GetType().Name, json);
                            await _incomingMessages.Writer.WriteAsync(message, cancellationToken);
                            OnMessageReceived?.Invoke(message);
                        }
                        else
                        {
                            _logger.LogWarning("Failed to deserialize message: {Json}", json);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (WebSocketException ex)
        {
            // Enriched context per the gate-logging rule. The bare "WebSocket error in
            // receive loop" line is invisible about cause and state; surface what we
            // can detect locally to make Railway-vs-network-vs-deliberate diagnosable
            // without correlating against server logs.
            var wsState = _webSocket?.State.ToString() ?? "null";
            var cancelled = cancellationToken.IsCancellationRequested;
            _logger.LogWarning(ex, "WebSocket error in receive loop (wsState={WsState}, cancelled={Cancelled}, wsErrorCode={Code}, reconnectEnabled={Reconnect})",
                wsState, cancelled, ex.WebSocketErrorCode, _reconnectEnabled);
            OnError?.Invoke(ex);
            OnDisconnected?.Invoke($"WebSocket error: {ex.Message}");

            // Auto-reconnect kick: WS died unexpectedly (Railway proxy, network blip,
            // server restart). Fire-and-forget so the receive loop can finish exiting;
            // ReconnectAsync's lock guard prevents concurrent attempts if a subscriber
            // (Home.razor) also kicks one off via OnDisconnected. _reconnectEnabled
            // gates this against deliberate DisconnectAsync teardown.
            if (_reconnectEnabled)
            {
                _ = Task.Run(() => ReconnectAsync(CancellationToken.None));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in receive loop");
            OnError?.Invoke(ex);
            OnDisconnected?.Invoke($"Receive loop error: {ex.Message}");

            // Same auto-reconnect rationale as the WebSocketException path above.
            if (_reconnectEnabled)
            {
                _ = Task.Run(() => ReconnectAsync(CancellationToken.None));
            }
        }
        finally
        {
            _incomingMessages.Writer.Complete();
            binaryBuffer.Dispose();
        }
    }

    /// <summary>
    /// Disconnect from the signaling server.
    /// </summary>
    public async Task DisconnectAsync()
    {
        // Disable auto-reconnect FIRST so the receive loop's WebSocketException catch
        // (which may fire mid-disposal as the WS closes) does not kick off a reconnect
        // loop on a deliberately-closed connection.
        _reconnectEnabled = false;
        await DisposeInternalAsync();
    }

    private async Task PingLoopAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await PingAsync(ct);
                    _logger.LogTrace("Keepalive ping sent");
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Keepalive ping failed");
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task DisposeInternalAsync()
    {
        // Stop ping timer
        _pingTimer?.Dispose();
        _pingTimer = null;

        // First, cancel the receive loop so it exits cleanly
        // This prevents WebSocketException from being raised as an error
        _cts?.Cancel();

        // Wait for ping loop and receive loop to exit
        if (_pingTask != null)
        {
            try { await _pingTask.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch { }
        }
        if (_receiveTask != null)
        {
            try
            {
                await _receiveTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                _logger.LogDebug("Receive loop did not exit in time");
            }
            catch
            {
                // Ignore other exceptions during cleanup
            }
        }

        // Now close the WebSocket (receive loop is already stopped)
        if (_webSocket != null)
        {
            var state = _webSocket.State;
            if (state == WebSocketState.Open || state == WebSocketState.CloseReceived)
            {
                try
                {
                    // Use a short timeout for the close handshake
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", closeCts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("WebSocket close handshake timed out");
                }
                catch (WebSocketException ex)
                {
                    _logger.LogDebug(ex, "WebSocket close handshake failed (expected during cancel)");
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Unexpected error during WebSocket close");
                }
            }
            else if (state == WebSocketState.Connecting)
            {
                _logger.LogDebug("Aborting WebSocket connection in progress");
                _webSocket.Abort();
            }
        }

        _webSocket?.Dispose();
        _webSocket = null;
        _cts?.Dispose();
        _cts = null;
        _receiveTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _writeLock.Dispose();
        _reconnectLock.Dispose();
    }
}
