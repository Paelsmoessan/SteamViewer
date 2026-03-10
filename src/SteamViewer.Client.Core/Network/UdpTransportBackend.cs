using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
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
    private byte[]? _turnHmacKey;
    private string? _turnNonce;
    private string? _turnRealm;
    private string? _turnUsername;
    private bool _useTurnRelay;
    private bool _active;
    private bool _disposed;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _echoCts;
    private Task? _receiveTask;
    private Task? _echoTask;
    private ushort _nextMessageId;

    // Reassembly buffer: msgId -> (fragments received, total expected, fragment data)
    private readonly ConcurrentDictionary<ushort, FragmentBuffer> _reassemblyBuffers = new();
    private Timer? _cleanupTimer;

    private const int MaxFragmentPayload = 1100; // Leave room for UDP/IP headers + encryption overhead
    private const int FragmentHeaderSize = 6;    // [2 msgId][2 fragIdx][2 totalFrags]
    private const int MaxMessageSize = 2 * 1024 * 1024; // 2MB sanity limit

    // FEC: 2D XOR matrix (SMPTE 2022-1 / SRT pattern)
    // FEC packets reuse standard header with fragIdx >= totalDataFrags
    // Payload: [1 cols][1 rows][3 totalMsgLen_BE][parity data padded to MaxFragmentPayload]
    private const int FecMinFragments = 10;
    private const int FecMetaSize = 5; // cols(1) + rows(1) + totalMsgLen(3)

    public event Action<byte[], int>? OnDataReceived;
    public event Action? OnDisconnected;
    public bool IsActive => _active && !_disposed;

    /// <summary>Local endpoint that was bound.</summary>
    public IPEndPoint? LocalEndPoint => _udpClient?.Client.LocalEndPoint as IPEndPoint;

    /// <summary>Reflexive (public) endpoint discovered via STUN.</summary>
    public IPEndPoint? ReflexiveEndPoint { get; private set; }

    /// <summary>TURN relay endpoint (if allocated).</summary>
    public IPEndPoint? TurnRelayEndPoint => _turnRelayEndPoint;

    /// <summary>Last endpoint that sent us a probe packet (0xFF). Used for asymmetric NAT recovery.</summary>
    public IPEndPoint? LastProbeReceivedFrom { get; private set; }

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
        _udpClient.Client.ReceiveBufferSize = 2 * 1024 * 1024; // 2MB — handles file transfer + video bursts
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

            // Wait for STUN response (2s timeout, cancellable — no abandoned tasks)
            using var stunCts = new CancellationTokenSource(2000);
            try
            {
                var result = await _udpClient.ReceiveAsync(stunCts.Token);
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
            catch (OperationCanceledException)
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

        // Diagnostic: how many bytes sitting in kernel buffer after STUN/TURN
        if (_udpClient != null)
            _logger.LogDebug("[UDP-DIAG] Post-STUN/TURN kernel buffer: {Available} bytes available", _udpClient.Available);

        // Start lightweight echo loop — echoes 0xFF probes from peer before our own probes complete.
        // This is critical for simultaneous hole-punching: peer's probe arrives, we echo it,
        // which creates the NAT binding for their return path.
        StartEchoLoop();
    }

    /// <summary>
    /// Receive a UDP packet from the TURN server only (filters by source endpoint).
    /// Discards stale STUN packets from other sources that may sit in the kernel buffer.
    /// </summary>
    private async Task<UdpReceiveResult> ReceiveFromTurnAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var result = await _udpClient!.ReceiveAsync(ct);
            if (_turnServerEndPoint != null && result.RemoteEndPoint.Equals(_turnServerEndPoint))
                return result;
            _logger.LogDebug("[UDP-DIAG] TURN: discarding {Len}byte packet from {Source} (expected {Expected})",
                result.Buffer.Length, result.RemoteEndPoint, _turnServerEndPoint);
        }
        throw new OperationCanceledException();
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

        // Wait for 401 response with nonce/realm — filter by TURN server source
        UdpReceiveResult resp401;
        using (var turn401Cts = new CancellationTokenSource(3000))
        {
            try
            {
                resp401 = await ReceiveFromTurnAsync(turn401Cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("TURN Allocate timed out waiting for 401");
                return;
            }
        }

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

        var nonce = Encoding.UTF8.GetString(nonceAttr.Value);
        var realm = Encoding.UTF8.GetString(realmAttr.Value);

        _logger.LogDebug("TURN: Got 401 with realm={Realm}, nonce={Nonce}", realm, nonce[..Math.Min(8, nonce.Length)] + "...");

        // Retry Allocate with auth credentials
        var allocateAuth = new STUNMessage(STUNMessageTypesEnum.Allocate);
        allocateAuth.Attributes.Add(new STUNAttribute(STUNAttributeTypesEnum.RequestedTransport, new byte[] { 17, 0, 0, 0 }));
        allocateAuth.Attributes.Add(new STUNAttribute(STUNAttributeTypesEnum.Username, Encoding.UTF8.GetBytes(username)));
        allocateAuth.Attributes.Add(new STUNAttribute(STUNAttributeTypesEnum.Nonce, Encoding.UTF8.GetBytes(nonce)));
        allocateAuth.Attributes.Add(new STUNAttribute(STUNAttributeTypesEnum.Realm, Encoding.UTF8.GetBytes(realm)));

        // RFC 5389 §15.4: long-term credential key = MD5(username:realm:password)
        var hmacKey = MD5.HashData(Encoding.UTF8.GetBytes($"{username}:{realm}:{credential}"));
        _turnHmacKey = hmacKey;
        _turnNonce = nonce;
        _turnRealm = realm;
        _turnUsername = username;
        var authBytes = allocateAuth.ToByteBuffer(hmacKey, false);
        await _udpClient.SendAsync(authBytes, authBytes.Length, _turnServerEndPoint);

        // Wait for Allocate success — filter by TURN server source
        UdpReceiveResult respAlloc;
        using (var turnAllocCts = new CancellationTokenSource(3000))
        {
            try
            {
                respAlloc = await ReceiveFromTurnAsync(turnAllocCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("TURN: Authenticated Allocate timed out");
                return;
            }
        }

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
            // Extract error code for diagnostics
            var errorAttr = msgAlloc?.Attributes
                .FirstOrDefault(a => a.AttributeType == STUNAttributeTypesEnum.ErrorCode);
            if (errorAttr != null)
            {
                // ErrorCode attribute: first 4 bytes = reserved(2) + class(1) + number(1)
                var errorCode = errorAttr.Value.Length >= 4
                    ? (errorAttr.Value[2] * 100 + errorAttr.Value[3])
                    : 0;
                var reason = errorAttr.Value.Length > 4
                    ? Encoding.UTF8.GetString(errorAttr.Value, 4, errorAttr.Value.Length - 4)
                    : "unknown";
                _logger.LogWarning("TURN Allocate error {Code}: {Reason}", errorCode, reason);
            }
            else
            {
                _logger.LogWarning("TURN Allocate failed: {Type}", msgAlloc?.Header.MessageType);
            }
        }
    }

    /// <summary>
    /// Start a lightweight echo loop that only responds to 0xFF probe packets.
    /// This runs before ConnectToPeer — it enables simultaneous hole-punching by
    /// echoing the peer's probes (creating a NAT binding) before our own probes complete.
    /// </summary>
    private void StartEchoLoop()
    {
        if (_udpClient == null) return;
        _echoCts = new CancellationTokenSource();
        _echoTask = Task.Run(() => EchoLoopAsync(_echoCts.Token));
        _logger.LogDebug("[UDP-DIAG] Echo loop started — will echo 0xFF probes from peer");
    }

    private async Task EchoLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _udpClient != null)
            {
                var result = await _udpClient.ReceiveAsync(ct);
                if (result.Buffer.Length == 1 && result.Buffer[0] == 0xFF)
                {
                    LastProbeReceivedFrom = result.RemoteEndPoint;
                    _logger.LogDebug("[UDP-DIAG] EchoLoop: echoing probe from {Source}", result.RemoteEndPoint);
                    try { await _udpClient.SendAsync(new byte[] { 0xFE }, 1, result.RemoteEndPoint); }
                    catch { }
                }
                else if (result.Buffer.Length <= 2)
                {
                    // Echo response (0xFE) or other small packet — ignore in echo loop
                }
                else
                {
                    _logger.LogDebug("[UDP-DIAG] EchoLoop: ignoring {Len}byte packet from {Source}", result.Buffer.Length, result.RemoteEndPoint);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[UDP-DIAG] Echo loop ended");
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
        // Cancel the echo loop — the full receive loop takes over
        _echoCts?.Cancel();

        _peerEndPoint = peerEndPoint;
        _useTurnRelay = useTurnRelay;
        _cts = new CancellationTokenSource();

        // Start full receive loop
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));

        // Start cleanup timer for stale reassembly buffers
        _cleanupTimer = new Timer(CleanupStaleBuffers, null, 1000, 1000);

        _active = true;
        _logger.LogDebug("[UDP-DIAG] ConnectToPeer: echo loop cancelled, full receive loop started");
        _logger.LogInformation("UDP transport connected to peer {Peer} (turnRelay={Turn})", peerEndPoint, useTurnRelay);
    }

    /// <summary>
    /// Probe the peer endpoint with a small packet. Returns true if a response is received within timeout.
    /// Uses cancellable ReceiveAsync to avoid orphaned tasks that steal packets from subsequent probes.
    /// </summary>
    public async Task<bool> ProbeAsync(IPEndPoint endpoint, TimeSpan timeout)
    {
        if (_udpClient == null) return false;

        // Cancel echo loop during probing — we'll consume packets directly via ReceiveAsync
        _echoCts?.Cancel();

        var localEp = _udpClient.Client.LocalEndPoint as IPEndPoint;
        _logger.LogDebug("[UDP-DIAG] ProbeAsync START: local={Local}, target={Target}, timeout={Timeout}ms, kernelBuffer={Available}bytes",
            localEp, endpoint, timeout.TotalMilliseconds, _udpClient.Available);

        using var probeCts = new CancellationTokenSource(timeout);
        var attempt = 0;
        var rng = new Random();
        try
        {
            // Burst phase: send 3 probes at ~0ms, ~15ms, ~30ms with jitter
            // Jitter prevents correlated timing where both sides' probes arrive before NAT bindings form
            for (int burst = 0; burst < 3; burst++)
            {
                attempt++;
                await _udpClient.SendAsync(new byte[] { 0xFF }, 1, endpoint);
                _logger.LogDebug("[UDP-DIAG] Probe #{Attempt} (burst) sent 0xFF to {Target}", attempt, endpoint);

                // Check for immediate response between burst probes
                using var burstCts = CancellationTokenSource.CreateLinkedTokenSource(probeCts.Token);
                burstCts.CancelAfter(10 + rng.Next(10)); // 10-20ms jitter between bursts
                try
                {
                    var result = await _udpClient.ReceiveAsync(burstCts.Token);
                    if (result.Buffer.Length <= 2)
                    {
                        _logger.LogInformation("UDP probe to {Endpoint} succeeded (burst #{Attempt})", endpoint, attempt);
                        StartEchoLoop(); // Restart echo loop for other candidates
                        return true;
                    }
                }
                catch (OperationCanceledException) when (!probeCts.Token.IsCancellationRequested) { }
            }

            // Slow retry phase: re-send every 150ms until overall timeout
            while (!probeCts.Token.IsCancellationRequested)
            {
                using var subCts = CancellationTokenSource.CreateLinkedTokenSource(probeCts.Token);
                subCts.CancelAfter(150);
                try
                {
                    var result = await _udpClient.ReceiveAsync(subCts.Token);
                    _logger.LogDebug("[UDP-DIAG] Probe received {Len}bytes from {Source} (first byte=0x{First:X2})",
                        result.Buffer.Length, result.RemoteEndPoint, result.Buffer.Length > 0 ? result.Buffer[0] : 0);

                    if (result.Buffer.Length <= 2)
                    {
                        _logger.LogInformation("UDP probe to {Endpoint} succeeded (attempt #{Attempt})", endpoint, attempt);
                        StartEchoLoop();
                        return true;
                    }
                    _logger.LogDebug("[UDP-DIAG] Ignoring large packet ({Len}bytes) — likely stale STUN/TURN", result.Buffer.Length);
                }
                catch (OperationCanceledException) when (!probeCts.Token.IsCancellationRequested)
                {
                    attempt++;
                    _logger.LogDebug("[UDP-DIAG] Probe #{Attempt} — no response after 150ms, resending to {Target}", attempt, endpoint);
                    try { await _udpClient.SendAsync(new byte[] { 0xFF }, 1, endpoint); }
                    catch (Exception sendEx) { _logger.LogDebug("[UDP-DIAG] Resend failed: {Error}", sendEx.Message); }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("[UDP-DIAG] ProbeAsync TIMEOUT after {Attempts} attempts to {Target}", attempt, endpoint);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[UDP-DIAG] ProbeAsync EXCEPTION to {Target} after {Attempts} attempts", endpoint, attempt);
        }

        StartEchoLoop(); // Restart echo loop for other candidates
        return false;
    }

    public async Task SendAsync(byte[] data, int offset, int length, CancellationToken ct = default)
    {
        if (_udpClient == null || _peerEndPoint == null || !_active) return;

        if (length > MaxMessageSize)
        {
            _logger.LogError("UDP message too large: {Size}KB exceeds {Max}KB limit — dropping",
                length / 1024, MaxMessageSize / 1024);
            return;
        }

        var msgId = _nextMessageId++;
        var totalFragments = (length + MaxFragmentPayload - 1) / MaxFragmentPayload;

        if (totalFragments > 10)
            _logger.LogTrace("UDP sending {Size}KB message in {Fragments} fragments (msgId={MsgId})",
                length / 1024, totalFragments, msgId);

        // Send data fragments
        for (int i = 0; i < totalFragments; i++)
        {
            var fragOffset = i * MaxFragmentPayload;
            var fragLen = Math.Min(MaxFragmentPayload, length - fragOffset);

            // Build fragment: [2 msgId][2 fragIdx][2 totalFrags][payload]
            var packet = new byte[FragmentHeaderSize + fragLen];
            WriteFragmentHeader(packet, msgId, i, totalFragments);
            Buffer.BlockCopy(data, offset + fragOffset, packet, FragmentHeaderSize, fragLen);

            await SendRawPacketAsync(packet);
        }

        // Generate and send FEC parity (2D XOR matrix)
        if (totalFragments >= FecMinFragments)
        {
            var (rows, cols) = ChooseFecMatrix(totalFragments);
            _logger.LogTrace("FEC: {Rows}x{Cols} matrix for {Frags} fragments (+{Extra} parity)",
                rows, cols, totalFragments, rows + cols);

            // Row parity — interleaved with column parity for burst protection
            var rowParities = new byte[rows][];
            for (int r = 0; r < rows; r++)
            {
                var parity = new byte[MaxFragmentPayload];
                for (int c = 0; c < cols; c++)
                {
                    var idx = r * cols + c;
                    if (idx >= totalFragments) break;
                    XorFragmentFromSource(parity, data, offset, length, idx);
                }
                rowParities[r] = parity;
            }

            var colParities = new byte[cols][];
            for (int c = 0; c < cols; c++)
            {
                var parity = new byte[MaxFragmentPayload];
                for (int r = 0; r < rows; r++)
                {
                    var idx = r * cols + c;
                    if (idx >= totalFragments) break;
                    XorFragmentFromSource(parity, data, offset, length, idx);
                }
                colParities[c] = parity;
            }

            // Send interleaved: RF0, CF0, RF1, CF1, ... (remaining RF/CF after shorter list ends)
            var maxPairs = Math.Max(rows, cols);
            for (int i = 0; i < maxPairs; i++)
            {
                if (i < rows)
                {
                    var fecPkt = BuildFecPacket(msgId, totalFragments + i, totalFragments,
                        (byte)cols, (byte)rows, length, rowParities[i]);
                    await SendRawPacketAsync(fecPkt);
                }
                if (i < cols)
                {
                    var fecPkt = BuildFecPacket(msgId, totalFragments + rows + i, totalFragments,
                        (byte)cols, (byte)rows, length, colParities[i]);
                    await SendRawPacketAsync(fecPkt);
                }
            }
        }
    }

    private async Task SendRawPacketAsync(byte[] packet)
    {
        if (_useTurnRelay && _turnServerEndPoint != null)
        {
            var sendInd = BuildSendIndication(_peerEndPoint!, packet);
            await _udpClient!.SendAsync(sendInd, sendInd.Length, _turnServerEndPoint);
        }
        else
        {
            await _udpClient!.SendAsync(packet, packet.Length, _peerEndPoint!);
        }
    }

    private static void WriteFragmentHeader(byte[] packet, ushort msgId, int fragIdx, int totalFrags)
    {
        packet[0] = (byte)(msgId >> 8);
        packet[1] = (byte)(msgId & 0xFF);
        packet[2] = (byte)(fragIdx >> 8);
        packet[3] = (byte)(fragIdx & 0xFF);
        packet[4] = (byte)(totalFrags >> 8);
        packet[5] = (byte)(totalFrags & 0xFF);
    }

    private static byte[] BuildFecPacket(ushort msgId, int fecFragIdx, int totalDataFrags,
        byte cols, byte rows, int totalMsgLen, byte[] parity)
    {
        var packet = new byte[FragmentHeaderSize + FecMetaSize + parity.Length];
        WriteFragmentHeader(packet, msgId, fecFragIdx, totalDataFrags);
        packet[FragmentHeaderSize] = cols;
        packet[FragmentHeaderSize + 1] = rows;
        packet[FragmentHeaderSize + 2] = (byte)(totalMsgLen >> 16);
        packet[FragmentHeaderSize + 3] = (byte)(totalMsgLen >> 8);
        packet[FragmentHeaderSize + 4] = (byte)(totalMsgLen & 0xFF);
        Buffer.BlockCopy(parity, 0, packet, FragmentHeaderSize + FecMetaSize, parity.Length);
        return packet;
    }

    private static void XorFragmentFromSource(byte[] parity, byte[] data, int dataOffset, int dataLength, int fragIdx)
    {
        var srcOffset = dataOffset + fragIdx * MaxFragmentPayload;
        var fragLen = Math.Min(MaxFragmentPayload, dataLength - fragIdx * MaxFragmentPayload);
        if (fragLen <= 0) return;
        for (int b = 0; b < fragLen; b++)
            parity[b] ^= data[srcOffset + b];
    }

    private static (int rows, int cols) ChooseFecMatrix(int fragmentCount)
    {
        var cols = (int)Math.Ceiling(Math.Sqrt(fragmentCount));
        cols = Math.Clamp(cols, 2, 30);
        var rows = (int)Math.Ceiling((double)fragmentCount / cols);
        return (rows, cols);
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

                // Handle probe/echo packets (single byte)
                if (length <= 1)
                {
                    // Echo probe packets (0xFF) so peer's ProbeAsync succeeds
                    // Use 0xFE for echo to prevent infinite loop between two ReceiveLoops
                    if (length == 1 && data[0] == 0xFF && _udpClient != null)
                    {
                        LastProbeReceivedFrom = result.RemoteEndPoint;
                        _logger.LogDebug("[UDP-DIAG] ReceiveLoop echoing probe from {Source}", result.RemoteEndPoint);
                        try { await _udpClient.SendAsync(new byte[] { 0xFE }, 1, result.RemoteEndPoint); }
                        catch { }
                    }
                    continue;
                }

                // Check if this is a TURN DataIndication (unwrap it)
                if (length > 20 && IsTurnDataIndication(data))
                {
                    (data, length) = UnwrapDataIndication(data, length);
                    if (data == null || length < FragmentHeaderSize) continue;
                }

                if (length < FragmentHeaderSize) continue;

                // Parse fragment header: [2 msgId][2 fragIdx][2 totalFrags]
                var msgId = (ushort)((data[0] << 8) | data[1]);
                var fragIndex = (data[2] << 8) | data[3];
                var totalFrags = (data[4] << 8) | data[5];

                // Single fragment — deliver immediately (no FEC possible)
                if (fragIndex == 0 && totalFrags == 1)
                {
                    var payload = new byte[length - FragmentHeaderSize];
                    Buffer.BlockCopy(data, FragmentHeaderSize, payload, 0, payload.Length);
                    OnDataReceived?.Invoke(payload, payload.Length);
                    continue;
                }

                var buffer = _reassemblyBuffers.GetOrAdd(msgId, _ => new FragmentBuffer(totalFrags));

                if (fragIndex >= totalFrags)
                {
                    // FEC parity packet — extract meta + parity data
                    if (length >= FragmentHeaderSize + FecMetaSize)
                    {
                        var fecIdx = fragIndex - totalFrags;
                        var cols = data[FragmentHeaderSize];
                        var rows = data[FragmentHeaderSize + 1];
                        var totalMsgLen = (data[FragmentHeaderSize + 2] << 16)
                            | (data[FragmentHeaderSize + 3] << 8)
                            | data[FragmentHeaderSize + 4];
                        var parityLen = length - FragmentHeaderSize - FecMetaSize;
                        var parityData = new byte[parityLen];
                        Buffer.BlockCopy(data, FragmentHeaderSize + FecMetaSize, parityData, 0, parityLen);

                        // Validate matrix dimensions
                        if (cols > 0 && cols <= 30 && rows > 0 && rows <= 30 && (int)rows * cols >= totalFrags)
                            buffer.AddFecPacket(fecIdx, parityData, cols, rows, totalMsgLen);
                    }
                }
                else
                {
                    // Data fragment
                    var payload = new byte[length - FragmentHeaderSize];
                    Buffer.BlockCopy(data, FragmentHeaderSize, payload, 0, payload.Length);
                    buffer.AddFragment(fragIndex, payload);
                }

                // Check if message is ready (direct completion or FEC recovery)
                if (buffer.IsComplete || buffer.TryRecover())
                {
                    _reassemblyBuffers.TryRemove(msgId, out _);
                    var assembled = buffer.Assemble();
                    if (buffer.WasRecovered)
                        _logger.LogDebug("FEC recovered message {MsgId} ({Size}KB, {Frags} fragments)",
                            msgId, assembled.Length / 1024, totalFrags);
                    OnDataReceived?.Invoke(assembled, assembled.Length);
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

    /// <summary>
    /// Create a TURN permission for the peer's relay address, then probe via TURN SendIndication.
    /// Returns true if the peer responds through the TURN relay.
    /// </summary>
    public async Task<bool> ProbeViaTurnRelayAsync(IPEndPoint peerRelayEndPoint, TimeSpan timeout)
    {
        if (_udpClient == null || _turnServerEndPoint == null || _turnHmacKey == null) return false;

        _logger.LogDebug("[UDP-DIAG] TURN relay probe: creating permission for {Peer}", peerRelayEndPoint);

        // Send CreatePermission for peer's relay address
        var permReq = new STUNMessage(STUNMessageTypesEnum.CreatePermission);
        permReq.Attributes.Add(new STUNXORAddressAttribute(STUNAttributeTypesEnum.XORPeerAddress, peerRelayEndPoint.Port, peerRelayEndPoint.Address));
        if (_turnUsername != null)
            permReq.Attributes.Add(new STUNAttribute(STUNAttributeTypesEnum.Username, Encoding.UTF8.GetBytes(_turnUsername)));
        if (_turnNonce != null)
            permReq.Attributes.Add(new STUNAttribute(STUNAttributeTypesEnum.Nonce, Encoding.UTF8.GetBytes(_turnNonce)));
        if (_turnRealm != null)
            permReq.Attributes.Add(new STUNAttribute(STUNAttributeTypesEnum.Realm, Encoding.UTF8.GetBytes(_turnRealm)));

        var permBytes = permReq.ToByteBuffer(_turnHmacKey, false);
        await _udpClient.SendAsync(permBytes, permBytes.Length, _turnServerEndPoint);

        // Wait for CreatePermission response
        using var permCts = new CancellationTokenSource(2000);
        try
        {
            var permResp = await ReceiveFromTurnAsync(permCts.Token);
            var permMsg = STUNMessage.ParseSTUNMessage(permResp.Buffer, permResp.Buffer.Length);
            if (permMsg?.Header.MessageType != STUNMessageTypesEnum.CreatePermissionSuccessResponse)
            {
                _logger.LogWarning("TURN CreatePermission failed: {Type}", permMsg?.Header.MessageType);
                return false;
            }
            _logger.LogDebug("[UDP-DIAG] TURN CreatePermission succeeded for {Peer}", peerRelayEndPoint);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("TURN CreatePermission timed out");
            return false;
        }

        // Now probe via SendIndication
        _echoCts?.Cancel();
        using var probeCts = new CancellationTokenSource(timeout);
        var attempt = 0;
        try
        {
            // Send 3 probe bursts via TURN
            for (int burst = 0; burst < 3; burst++)
            {
                attempt++;
                var probeInd = BuildSendIndication(peerRelayEndPoint, new byte[] { 0xFF });
                await _udpClient.SendAsync(probeInd, probeInd.Length, _turnServerEndPoint);
                _logger.LogDebug("[UDP-DIAG] TURN probe #{Attempt} sent to {Peer} via {Turn}", attempt, peerRelayEndPoint, _turnServerEndPoint);

                using var burstCts = CancellationTokenSource.CreateLinkedTokenSource(probeCts.Token);
                burstCts.CancelAfter(200);
                try
                {
                    var result = await _udpClient.ReceiveAsync(burstCts.Token);
                    // Could be a DataIndication containing the echo, or a direct echo
                    if (result.Buffer.Length <= 2 ||
                        (IsTurnDataIndication(result.Buffer) && result.Buffer.Length < 40))
                    {
                        _logger.LogInformation("TURN relay probe to {Peer} succeeded", peerRelayEndPoint);
                        StartEchoLoop();
                        return true;
                    }
                }
                catch (OperationCanceledException) when (!probeCts.Token.IsCancellationRequested) { }
            }

            // Slow retry
            while (!probeCts.Token.IsCancellationRequested)
            {
                using var subCts = CancellationTokenSource.CreateLinkedTokenSource(probeCts.Token);
                subCts.CancelAfter(300);
                try
                {
                    var result = await _udpClient.ReceiveAsync(subCts.Token);
                    if (result.Buffer.Length <= 2 ||
                        (IsTurnDataIndication(result.Buffer) && result.Buffer.Length < 40))
                    {
                        _logger.LogInformation("TURN relay probe to {Peer} succeeded (attempt #{Attempt})", peerRelayEndPoint, attempt);
                        StartEchoLoop();
                        return true;
                    }
                }
                catch (OperationCanceledException) when (!probeCts.Token.IsCancellationRequested)
                {
                    attempt++;
                    var probeInd = BuildSendIndication(peerRelayEndPoint, new byte[] { 0xFF });
                    try { await _udpClient.SendAsync(probeInd, probeInd.Length, _turnServerEndPoint); }
                    catch { }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("[UDP-DIAG] TURN probe TIMEOUT after {Attempts} attempts to {Peer}", attempt, peerRelayEndPoint);
        }

        StartEchoLoop();
        return false;
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
            if (now - kvp.Value.CreatedAt > 500) // 500ms timeout (large keyframes need more time)
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
        _echoCts?.Cancel();
        _cts?.Cancel();

        if (_echoTask != null)
            try { await _echoTask; } catch { }
        if (_receiveTask != null)
            try { await _receiveTask; } catch { }

        _udpClient?.Dispose();
        _echoCts?.Dispose();
        _cts?.Dispose();
    }

    private sealed class FragmentBuffer
    {
        private readonly byte[]?[] _fragments;
        private readonly int _totalFragments;
        private int _receivedCount;
        public long CreatedAt { get; } = Environment.TickCount64;

        // FEC state
        private byte[]?[]? _rowParity;
        private byte[]?[]? _colParity;
        private int _matrixCols;
        private int _matrixRows;
        private int _totalMsgLen;
        private bool _hasFec;

        public bool IsComplete => _receivedCount >= _totalFragments;
        public bool WasRecovered { get; private set; }

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

        public void AddFecPacket(int fecIndex, byte[] parityData, byte cols, byte rows, int totalMsgLen)
        {
            if (!_hasFec)
            {
                _matrixCols = cols;
                _matrixRows = rows;
                _totalMsgLen = totalMsgLen;
                _rowParity = new byte[rows][];
                _colParity = new byte[cols][];
                _hasFec = true;
            }

            if (fecIndex < _matrixRows)
                _rowParity![fecIndex] = parityData;
            else if (fecIndex - _matrixRows < _matrixCols)
                _colParity![fecIndex - _matrixRows] = parityData;
        }

        /// <summary>
        /// Attempt 2D XOR recovery. Iterates row then column recovery until
        /// no more progress or all fragments recovered.
        /// </summary>
        public bool TryRecover()
        {
            if (!_hasFec || IsComplete) return IsComplete;

            bool progress;
            do
            {
                progress = false;

                // Row recovery
                for (int r = 0; r < _matrixRows; r++)
                {
                    if (_rowParity?[r] == null) continue;
                    int missingIdx = -1, missingCount = 0;
                    for (int c = 0; c < _matrixCols; c++)
                    {
                        var idx = r * _matrixCols + c;
                        if (idx >= _totalFragments) break;
                        if (_fragments[idx] == null)
                        {
                            missingIdx = idx;
                            if (++missingCount > 1) break;
                        }
                    }
                    if (missingCount == 1 && missingIdx >= 0)
                    {
                        var recovered = (byte[])_rowParity[r]!.Clone();
                        for (int c = 0; c < _matrixCols; c++)
                        {
                            var idx = r * _matrixCols + c;
                            if (idx >= _totalFragments) break;
                            if (idx == missingIdx) continue;
                            XorInto(recovered, _fragments[idx]!);
                        }
                        TrimRecoveredFragment(ref recovered, missingIdx);
                        _fragments[missingIdx] = recovered;
                        Interlocked.Increment(ref _receivedCount);
                        progress = true;
                        WasRecovered = true;
                    }
                }

                // Column recovery
                for (int c = 0; c < _matrixCols; c++)
                {
                    if (_colParity?[c] == null) continue;
                    int missingIdx = -1, missingCount = 0;
                    for (int r = 0; r < _matrixRows; r++)
                    {
                        var idx = r * _matrixCols + c;
                        if (idx >= _totalFragments) break;
                        if (_fragments[idx] == null)
                        {
                            missingIdx = idx;
                            if (++missingCount > 1) break;
                        }
                    }
                    if (missingCount == 1 && missingIdx >= 0)
                    {
                        var recovered = (byte[])_colParity[c]!.Clone();
                        for (int r = 0; r < _matrixRows; r++)
                        {
                            var idx = r * _matrixCols + c;
                            if (idx >= _totalFragments) break;
                            if (idx == missingIdx) continue;
                            XorInto(recovered, _fragments[idx]!);
                        }
                        TrimRecoveredFragment(ref recovered, missingIdx);
                        _fragments[missingIdx] = recovered;
                        Interlocked.Increment(ref _receivedCount);
                        progress = true;
                        WasRecovered = true;
                    }
                }
            } while (progress && !IsComplete);

            return IsComplete;
        }

        /// <summary>
        /// Trim recovered fragment to correct size. The last fragment may be shorter
        /// than MaxFragmentPayload; recovered parity data is always MaxFragmentPayload.
        /// </summary>
        private void TrimRecoveredFragment(ref byte[] recovered, int fragIdx)
        {
            if (_totalMsgLen <= 0 || fragIdx != _totalFragments - 1) return;
            var expectedLen = _totalMsgLen - fragIdx * MaxFragmentPayload;
            if (expectedLen > 0 && expectedLen < recovered.Length)
                Array.Resize(ref recovered, expectedLen);
        }

        private static void XorInto(byte[] target, byte[] source)
        {
            var len = Math.Min(target.Length, source.Length);
            for (int i = 0; i < len; i++)
                target[i] ^= source[i];
        }

        public byte[] Assemble()
        {
            // Use totalMsgLen if available (FEC recovery may have padded fragments)
            if (WasRecovered && _totalMsgLen > 0)
            {
                var result = new byte[_totalMsgLen];
                var offset = 0;
                foreach (var frag in _fragments)
                {
                    if (frag == null) continue;
                    var copyLen = Math.Min(frag.Length, _totalMsgLen - offset);
                    if (copyLen <= 0) break;
                    Buffer.BlockCopy(frag, 0, result, offset, copyLen);
                    offset += copyLen;
                }
                return result;
            }

            var totalLen = _fragments.Where(f => f != null).Sum(f => f!.Length);
            var buf = new byte[totalLen];
            var off = 0;
            foreach (var frag in _fragments)
            {
                if (frag == null) continue;
                Buffer.BlockCopy(frag, 0, buf, off, frag.Length);
                off += frag.Length;
            }
            return buf;
        }
    }
}
