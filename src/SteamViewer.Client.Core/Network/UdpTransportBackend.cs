using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace SteamViewer.Client.Core.Network;

/// <summary>
/// Transport backend that sends data over direct UDP (STUN hole-punch) or TURN relay.
/// Phase 2 transport — lower latency than WebSocket relay.
///
/// Connection cascade:
/// 1. STUN binding to discover reflexive (public) endpoint
/// 2. TURN allocation to get relay endpoint (fallback)
/// 3. Exchange endpoints via signaling
/// 4. Probe direct UDP → if success, use direct
/// 5. If direct fails → use TURN relay
///
/// Fragmentation: messages >1100 bytes are split into fragments with 4-byte header.
/// Fragment header: [2 bytes msgId][1 byte fragIndex][1 byte totalFrags]
/// </summary>
public sealed class UdpTransportBackend : ITransportBackend
{
    private readonly ILogger _logger;
    private UdpClient? _udpClient;
    private IPEndPoint? _peerEndPoint;
    private IPEndPoint? _turnServerEndPoint;
    private IPEndPoint? _turnRelayEndPoint;
    private bool _useTurnRelay;
    private bool _active;
    private bool _disposed;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private ushort _nextMessageId;

    // Reassembly buffer: msgId -> (fragments received, total expected, fragment data)
    private readonly ConcurrentDictionary<ushort, FragmentBuffer> _reassemblyBuffers = new();
    private Timer? _cleanupTimer;

    private const int MaxFragmentPayload = 1100; // Leave room for UDP/IP headers + encryption overhead
    private const int FragmentHeaderSize = 4;    // [2 msgId][1 fragIdx][1 totalFrags]

    public event Action<byte[], int>? OnDataReceived;
    public bool IsActive => _active && !_disposed;

    /// <summary>Local endpoint that was bound.</summary>
    public IPEndPoint? LocalEndPoint => _udpClient?.Client.LocalEndPoint as IPEndPoint;

    /// <summary>Reflexive (public) endpoint discovered via STUN.</summary>
    public IPEndPoint? ReflexiveEndPoint { get; private set; }

    /// <summary>TURN relay endpoint (if allocated).</summary>
    public IPEndPoint? TurnRelayEndPoint => _turnRelayEndPoint;

    public UdpTransportBackend(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Bind a local UDP socket and discover public endpoint via STUN.
    /// Optionally allocate a TURN relay endpoint.
    /// </summary>
    /// <param name="turnServerUri">TURN server URI (e.g., "turn:host:port")</param>
    /// <param name="username">TURN username</param>
    /// <param name="credential">TURN credential</param>
    public async Task InitializeAsync(string? turnServerUri = null, string? username = null, string? credential = null)
    {
        _udpClient = new UdpClient(0); // Bind to ephemeral port
        var localPort = ((IPEndPoint)_udpClient.Client.LocalEndPoint!).Port;
        _logger.LogInformation("UDP transport bound to local port {Port}", localPort);

        // STUN binding to discover reflexive address
        try
        {
            var stunServer = new IPEndPoint(
                (await Dns.GetHostAddressesAsync("stun.l.google.com")).First(a => a.AddressFamily == AddressFamily.InterNetwork),
                19302);

            var stunRequest = new STUNMessage(STUNMessageTypesEnum.BindingRequest);
            var stunBytes = stunRequest.ToByteBuffer(null, false);
            await _udpClient.SendAsync(stunBytes, stunBytes.Length, stunServer);

            // Wait for STUN response (2s timeout)
            var receiveTask = _udpClient.ReceiveAsync();
            if (await Task.WhenAny(receiveTask, Task.Delay(2000)) == receiveTask)
            {
                var result = await receiveTask;
                var response = STUNMessage.ParseSTUNMessage(result.Buffer, result.Buffer.Length);
                if (response != null)
                {
                    var mapped = response.Attributes
                        .FirstOrDefault(a => a.AttributeType == STUNAttributeTypesEnum.XORMappedAddress)
                        as STUNXORAddressAttribute;
                    if (mapped != null)
                    {
                        ReflexiveEndPoint = new IPEndPoint(mapped.Address, mapped.Port);
                        _logger.LogInformation("STUN reflexive endpoint: {Endpoint}", ReflexiveEndPoint);
                    }
                }
            }
            else
            {
                _logger.LogWarning("STUN binding timed out");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "STUN binding failed");
        }

        // TURN allocation (if configured)
        if (!string.IsNullOrEmpty(turnServerUri) && !string.IsNullOrEmpty(username))
        {
            try
            {
                await AllocateTurnAsync(turnServerUri, username, credential ?? "");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TURN allocation failed");
            }
        }
    }

    private async Task AllocateTurnAsync(string turnUri, string username, string credential)
    {
        // Parse turn URI: "turn:host:port"
        var uri = turnUri.Replace("turn:", "").Replace("turns:", "");
        var parts = uri.Split(':');
        var host = parts[0];
        var port = parts.Length > 1 ? int.Parse(parts[1].Split('?')[0]) : 3478;

        var addresses = await Dns.GetHostAddressesAsync(host);
        var turnAddr = addresses.First(a => a.AddressFamily == AddressFamily.InterNetwork);
        _turnServerEndPoint = new IPEndPoint(turnAddr, port);

        _logger.LogInformation("Attempting TURN allocation to {Server}", _turnServerEndPoint);

        // Send Allocate request (will get 401 first, then retry with auth)
        var allocateReq = new STUNMessage(STUNMessageTypesEnum.Allocate);
        allocateReq.Attributes.Add(new STUNAttribute(STUNAttributeTypesEnum.RequestedTransport, new byte[] { 17, 0, 0, 0 })); // UDP = 17

        var allocateBytes = allocateReq.ToByteBuffer(null, false);
        await _udpClient!.SendAsync(allocateBytes, allocateBytes.Length, _turnServerEndPoint);

        // Wait for 401 response with nonce/realm
        var resp401Task = _udpClient.ReceiveAsync();
        if (await Task.WhenAny(resp401Task, Task.Delay(3000)) != resp401Task)
        {
            _logger.LogWarning("TURN Allocate timed out waiting for 401");
            return;
        }

        var resp401 = await resp401Task;
        var msg401 = STUNMessage.ParseSTUNMessage(resp401.Buffer, resp401.Buffer.Length);
        if (msg401 == null)
        {
            _logger.LogWarning("TURN: Could not parse 401 response");
            return;
        }

        var nonceAttr = msg401.Attributes.FirstOrDefault(a => a.AttributeType == STUNAttributeTypesEnum.Nonce);
        var realmAttr = msg401.Attributes.FirstOrDefault(a => a.AttributeType == STUNAttributeTypesEnum.Realm);

        if (nonceAttr == null || realmAttr == null)
        {
            _logger.LogWarning("TURN: 401 response missing nonce or realm");
            return;
        }

        var nonce = System.Text.Encoding.UTF8.GetString(nonceAttr.Value);
        var realm = System.Text.Encoding.UTF8.GetString(realmAttr.Value);

        // Retry Allocate with auth credentials
        var allocateAuth = new STUNMessage(STUNMessageTypesEnum.Allocate);
        allocateAuth.Attributes.Add(new STUNAttribute(STUNAttributeTypesEnum.RequestedTransport, new byte[] { 17, 0, 0, 0 }));
        allocateAuth.Attributes.Add(new STUNAttribute(STUNAttributeTypesEnum.Username, System.Text.Encoding.UTF8.GetBytes(username)));
        allocateAuth.Attributes.Add(new STUNAttribute(STUNAttributeTypesEnum.Nonce, System.Text.Encoding.UTF8.GetBytes(nonce)));
        allocateAuth.Attributes.Add(new STUNAttribute(STUNAttributeTypesEnum.Realm, System.Text.Encoding.UTF8.GetBytes(realm)));

        var authBytes = allocateAuth.ToByteBufferStringKey(credential, false);
        await _udpClient.SendAsync(authBytes, authBytes.Length, _turnServerEndPoint);

        // Wait for Allocate success
        var respAllocTask = _udpClient.ReceiveAsync();
        if (await Task.WhenAny(respAllocTask, Task.Delay(3000)) != respAllocTask)
        {
            _logger.LogWarning("TURN: Authenticated Allocate timed out");
            return;
        }

        var respAlloc = await respAllocTask;
        var msgAlloc = STUNMessage.ParseSTUNMessage(respAlloc.Buffer, respAlloc.Buffer.Length);
        if (msgAlloc?.Header.MessageType == STUNMessageTypesEnum.AllocateSuccessResponse)
        {
            var relayAddr = msgAlloc.Attributes
                .FirstOrDefault(a => a.AttributeType == STUNAttributeTypesEnum.XORRelayedAddress)
                as STUNXORAddressAttribute;
            if (relayAddr != null)
            {
                _turnRelayEndPoint = new IPEndPoint(relayAddr.Address, relayAddr.Port);
                _logger.LogInformation("TURN allocation successful. Relay: {Relay}", _turnRelayEndPoint);
            }
        }
        else
        {
            _logger.LogWarning("TURN Allocate failed: {Type}", msgAlloc?.Header.MessageType);
        }
    }

    /// <summary>
    /// Set the peer endpoint and start receiving.
    /// Call after endpoint exchange via signaling.
    /// </summary>
    /// <param name="peerEndPoint">The peer's endpoint (direct or TURN relay).</param>
    /// <param name="useTurnRelay">If true, wrap data in TURN SendIndication.</param>
    public void ConnectToPeer(IPEndPoint peerEndPoint, bool useTurnRelay = false)
    {
        _peerEndPoint = peerEndPoint;
        _useTurnRelay = useTurnRelay;
        _cts = new CancellationTokenSource();

        // Start receive loop
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));

        // Start cleanup timer for stale reassembly buffers
        _cleanupTimer = new Timer(CleanupStaleBuffers, null, 1000, 1000);

        _active = true;
        _logger.LogInformation("UDP transport connected to peer {Peer} (turnRelay={Turn})", peerEndPoint, useTurnRelay);
    }

    /// <summary>
    /// Probe the peer endpoint with a small packet. Returns true if a response is received within timeout.
    /// </summary>
    public async Task<bool> ProbeAsync(IPEndPoint endpoint, TimeSpan timeout)
    {
        if (_udpClient == null) return false;

        try
        {
            // Send a small probe packet (just a zero byte)
            var probe = new byte[] { 0xFF };
            await _udpClient.SendAsync(probe, 1, endpoint);

            // Wait for any response
            var cts = new CancellationTokenSource(timeout);
            var receiveTask = _udpClient.ReceiveAsync();
            if (await Task.WhenAny(receiveTask, Task.Delay(timeout)) == receiveTask)
            {
                _logger.LogInformation("UDP probe to {Endpoint} succeeded", endpoint);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UDP probe to {Endpoint} failed", endpoint);
        }

        return false;
    }

    public async Task SendAsync(byte[] data, int offset, int length, CancellationToken ct = default)
    {
        if (_udpClient == null || _peerEndPoint == null || !_active) return;

        var msgId = _nextMessageId++;
        var totalFragments = (length + MaxFragmentPayload - 1) / MaxFragmentPayload;
        if (totalFragments > 255) totalFragments = 255; // Cap at 255 fragments (281KB max message)

        for (int i = 0; i < totalFragments; i++)
        {
            var fragOffset = i * MaxFragmentPayload;
            var fragLen = Math.Min(MaxFragmentPayload, length - fragOffset);

            // Build fragment: [2 msgId][1 fragIdx][1 totalFrags][payload]
            var packet = new byte[FragmentHeaderSize + fragLen];
            packet[0] = (byte)(msgId >> 8);
            packet[1] = (byte)(msgId & 0xFF);
            packet[2] = (byte)i;
            packet[3] = (byte)totalFragments;
            Buffer.BlockCopy(data, offset + fragOffset, packet, FragmentHeaderSize, fragLen);

            if (_useTurnRelay && _turnServerEndPoint != null)
            {
                // Wrap in TURN SendIndication
                var sendInd = BuildSendIndication(_peerEndPoint, packet);
                await _udpClient.SendAsync(sendInd, sendInd.Length, _turnServerEndPoint);
            }
            else
            {
                await _udpClient.SendAsync(packet, packet.Length, _peerEndPoint);
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _udpClient != null)
            {
                var result = await _udpClient.ReceiveAsync(ct);
                var data = result.Buffer;
                var length = data.Length;

                // Skip probe responses (single byte)
                if (length <= 1) continue;

                // Check if this is a TURN DataIndication (unwrap it)
                if (length > 20 && IsTurnDataIndication(data))
                {
                    (data, length) = UnwrapDataIndication(data, length);
                    if (data == null || length < FragmentHeaderSize) continue;
                }

                if (length < FragmentHeaderSize) continue;

                // Parse fragment header
                var msgId = (ushort)((data[0] << 8) | data[1]);
                var fragIndex = data[2];
                var totalFrags = data[3];
                var payload = new byte[length - FragmentHeaderSize];
                Buffer.BlockCopy(data, FragmentHeaderSize, payload, 0, payload.Length);

                if (totalFrags == 1)
                {
                    // Single fragment — deliver immediately
                    OnDataReceived?.Invoke(payload, payload.Length);
                }
                else
                {
                    // Multi-fragment — add to reassembly buffer
                    var buffer = _reassemblyBuffers.GetOrAdd(msgId, _ => new FragmentBuffer(totalFrags));
                    buffer.AddFragment(fragIndex, payload);

                    if (buffer.IsComplete)
                    {
                        _reassemblyBuffers.TryRemove(msgId, out _);
                        var assembled = buffer.Assemble();
                        OnDataReceived?.Invoke(assembled, assembled.Length);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UDP receive loop error");
        }
    }

    private static byte[] BuildSendIndication(IPEndPoint peer, byte[] data)
    {
        // Build a TURN SendIndication message manually
        // Type: 0x0016 (SendIndication), Length: varies
        // Attributes: XOR-PEER-ADDRESS, DATA
        var msg = new STUNMessage(STUNMessageTypesEnum.SendIndication);
        msg.Attributes.Add(new STUNXORAddressAttribute(STUNAttributeTypesEnum.XORPeerAddress, peer.Port, peer.Address));
        msg.Attributes.Add(new STUNAttribute(STUNAttributeTypesEnum.Data, data));
        return msg.ToByteBuffer(null, false);
    }

    private static bool IsTurnDataIndication(byte[] data)
    {
        if (data.Length < 4) return false;
        // STUN message type for DataIndication = 0x0017
        var msgType = (data[0] << 8) | data[1];
        return msgType == (int)STUNMessageTypesEnum.DataIndication;
    }

    private static (byte[]? data, int length) UnwrapDataIndication(byte[] raw, int rawLength)
    {
        var msg = STUNMessage.ParseSTUNMessage(raw, rawLength);
        if (msg == null) return (null, 0);
        var dataAttr = msg.Attributes.FirstOrDefault(a => a.AttributeType == STUNAttributeTypesEnum.Data);
        if (dataAttr?.Value == null) return (null, 0);
        return (dataAttr.Value, dataAttr.Value.Length);
    }

    private void CleanupStaleBuffers(object? state)
    {
        var now = Environment.TickCount64;
        foreach (var kvp in _reassemblyBuffers)
        {
            if (now - kvp.Value.CreatedAt > 200) // 200ms timeout
            {
                _reassemblyBuffers.TryRemove(kvp.Key, out _);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _active = false;

        _cleanupTimer?.Dispose();
        _cts?.Cancel();

        if (_receiveTask != null)
            try { await _receiveTask; } catch { }

        _udpClient?.Dispose();
        _cts?.Dispose();
    }

    private sealed class FragmentBuffer
    {
        private readonly byte[]?[] _fragments;
        private readonly int _totalFragments;
        private int _receivedCount;
        public long CreatedAt { get; } = Environment.TickCount64;

        public bool IsComplete => _receivedCount >= _totalFragments;

        public FragmentBuffer(int totalFragments)
        {
            _totalFragments = totalFragments;
            _fragments = new byte[totalFragments][];
        }

        public void AddFragment(int index, byte[] data)
        {
            if (index < 0 || index >= _totalFragments) return;
            if (_fragments[index] == null)
            {
                _fragments[index] = data;
                Interlocked.Increment(ref _receivedCount);
            }
        }

        public byte[] Assemble()
        {
            var totalLen = _fragments.Where(f => f != null).Sum(f => f!.Length);
            var result = new byte[totalLen];
            var offset = 0;
            foreach (var frag in _fragments)
            {
                if (frag == null) continue;
                Buffer.BlockCopy(frag, 0, result, offset, frag.Length);
                offset += frag.Length;
            }
            return result;
        }
    }
}
