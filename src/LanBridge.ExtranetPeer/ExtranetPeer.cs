using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using LanBridge.Common.Network;
using LanBridge.Common.Protocol;

namespace LanBridge.ExtranetPeer;

public class ClientConfig
{
    public string NodeId { get; set; } = "extranet-client-001";
    public string SignalingServerHost { get; set; } = "127.0.0.1";
    public int SignalingServerPort { get; set; } = 9000;
    public string StunServerHost { get; set; } = "127.0.0.1";
    public int StunServerPort { get; set; } = 9001;
    public int StunAlternateServerPort { get; set; } = 9003;
    public string TargetNodeId { get; set; } = "intranet-peer-001";
    public int LocalProxyPort { get; set; } = 8554;
    public int UdpPort { get; set; }
    public int HolePunchTimeoutMs { get; set; } = 10000;
    public bool EnableRelayFallback { get; set; } = true;
    public bool Verbose { get; set; }
    public List<TunnelMapping> Mappings { get; set; } = new();
}

public class TunnelMapping
{
    public int LocalPort { get; set; }
    public string TargetHost { get; set; } = string.Empty;
    public int TargetPort { get; set; }
    public string Protocol { get; set; } = "tcp";

    public string Target => string.IsNullOrWhiteSpace(TargetHost) || TargetPort <= 0
        ? string.Empty
        : $"{TargetHost}:{TargetPort}:{Protocol}";
}

public enum ConnectionState
{
    Disconnected,
    Connecting,
    HolePunching,
    Connected,
    RelayMode,
    Error
}

public class ExtranetPeer : IDisposable
{
    private readonly ClientConfig _config;
    private readonly ConnectionNegotiator _connection;
    private readonly List<TcpListener> _localProxies = new();
    private readonly ConcurrentDictionary<uint, TcpClient> _localClients = new();
    private readonly List<UdpClient> _localUdpProxies = new();
    private readonly ConcurrentDictionary<uint, UdpSession> _udpSessions = new();
    private readonly ConcurrentDictionary<(int LocalPort, IPEndPoint ClientEndPoint), UdpSession> _udpClientSessions = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _isRunning;
    private ConnectionState _state = ConnectionState.Disconnected;

    public event Action<string>? OnStatusChanged;
    public event Action<byte[], int>? OnDataReceived;

    public ConnectionState State => _state;
    public bool IsConnected => _connection.IsConnected;

    private sealed class UdpSession
    {
        public required uint StreamId { get; init; }
        public required IPEndPoint ClientEndPoint { get; init; }
        public required UdpClient Listener { get; init; }
        public required int LocalPort { get; init; }
        public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
    }

    public ExtranetPeer(ClientConfig config)
    {
        _config = config;
        if (_config.Mappings.Count == 0)
        {
            _config.Mappings.Add(new TunnelMapping { LocalPort = _config.LocalProxyPort });
        }

        _connection = new ConnectionNegotiator(new PeerConnectionOptions
        {
            Role = PeerConnectionRole.Extranet,
            NodeId = _config.NodeId,
            SignalingServerHost = _config.SignalingServerHost,
            SignalingServerPort = _config.SignalingServerPort,
            StunServerHost = _config.StunServerHost,
            StunServerPort = _config.StunServerPort,
            StunAlternateServerPort = _config.StunAlternateServerPort,
            TargetNodeId = _config.TargetNodeId,
            UdpPort = _config.UdpPort,
            HolePunchTimeoutMs = _config.HolePunchTimeoutMs,
            EnableRelayFallback = _config.EnableRelayFallback,
            Verbose = _config.Verbose
        });
        _connection.OnStatusChanged += HandleConnectionStatus;
        _connection.OnModeChanged += HandleTransportModeChanged;
        _connection.OnSignalingDisconnected += () => UpdateState(ConnectionState.Disconnected);
        _connection.OnDataReceived += HandleTunnelData;
    }

    public async Task StartAsync()
    {
        _isRunning = true;
        UpdateState(ConnectionState.Connecting);

        try
        {
            await _connection.StartAsync();
            await StartLocalProxyAsync();
            StartUdpSessionCleaner();

            while (_isRunning)
            {
                await Task.Delay(1000, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            UpdateState(ConnectionState.Error);
            OnStatusChanged?.Invoke($"Error: {ex.Message}");
            throw;
        }
    }

    private void HandleConnectionStatus(string status)
    {
        if (status.StartsWith("State: HolePunching", StringComparison.OrdinalIgnoreCase))
        {
            UpdateState(ConnectionState.HolePunching);
            return;
        }

        OnStatusChanged?.Invoke(status);
    }

    private void HandleTransportModeChanged(PeerTransportMode mode)
    {
        switch (mode)
        {
            case PeerTransportMode.P2pDirect:
                UpdateState(ConnectionState.Connected);
                break;
            case PeerTransportMode.Relay:
                UpdateState(ConnectionState.RelayMode);
                break;
            case PeerTransportMode.None:
                if (_state != ConnectionState.Connecting)
                {
                    UpdateState(ConnectionState.Disconnected);
                }
                break;
        }
    }

    private async Task StartLocalProxyAsync()
    {
        foreach (var mapping in _config.Mappings)
        {
            if (string.Equals(mapping.Protocol, "udp", StringComparison.OrdinalIgnoreCase))
            {
                await StartUdpLocalProxyAsync(mapping);
            }
            else
            {
                try
                {
                    var listener = new TcpListener(IPAddress.Loopback, mapping.LocalPort);
                    listener.Start();
                    _localProxies.Add(listener);
                    OnStatusChanged?.Invoke($"Local TCP proxy started on 127.0.0.1:{mapping.LocalPort}" +
                                            (string.IsNullOrWhiteSpace(mapping.Target) ? " -> intranet default target" : $" -> {mapping.Target}"));
                    _ = Task.Run(() => AcceptLocalClientsAsync(listener, mapping));
                }
                catch (SocketException ex) when (mapping.LocalPort == 1554)
                {
                    OnStatusChanged?.Invoke($"Local TCP proxy port 1554 unavailable ({ex.Message}); retrying on 8554");
                    mapping.LocalPort = 8554;
                    var listener = new TcpListener(IPAddress.Loopback, mapping.LocalPort);
                    listener.Start();
                    _localProxies.Add(listener);
                    OnStatusChanged?.Invoke($"Local TCP proxy started on 127.0.0.1:{mapping.LocalPort}");
                    _ = Task.Run(() => AcceptLocalClientsAsync(listener, mapping));
                }
            }
        }
    }

    private async Task StartUdpLocalProxyAsync(TunnelMapping mapping)
    {
        try
        {
            var localEp = new IPEndPoint(IPAddress.Loopback, mapping.LocalPort);
            var udpClient = new UdpClient(localEp);
            _localUdpProxies.Add(udpClient);
            OnStatusChanged?.Invoke($"Local UDP proxy started on 127.0.0.1:{mapping.LocalPort}" +
                                    (string.IsNullOrWhiteSpace(mapping.Target) ? " -> intranet default target" : $" -> {mapping.Target}"));
            _ = Task.Run(() => ReceiveLocalUdpPacketsAsync(udpClient, mapping));
        }
        catch (Exception ex)
        {
            OnStatusChanged?.Invoke($"Failed to start local UDP proxy on {mapping.LocalPort}: {ex.Message}");
        }
    }

    private async Task ReceiveLocalUdpPacketsAsync(UdpClient udpClient, TunnelMapping mapping)
    {
        while (_isRunning)
        {
            try
            {
                var result = await udpClient.ReceiveAsync(_cts.Token);
                var clientEp = result.RemoteEndPoint;
                var data = result.Buffer;

                var sessionKey = (mapping.LocalPort, clientEp);
                if (!_udpClientSessions.TryGetValue(sessionKey, out var session))
                {
                    var streamId = NextStreamId();
                    session = new UdpSession
                    {
                        StreamId = streamId,
                        ClientEndPoint = clientEp,
                        Listener = udpClient,
                        LocalPort = mapping.LocalPort,
                        LastActivityUtc = DateTime.UtcNow
                    };

                    if (_udpSessions.TryAdd(streamId, session))
                    {
                        _udpClientSessions[sessionKey] = session;
                        OnStatusChanged?.Invoke($"New UDP virtual session: stream {streamId} for {clientEp} on port {mapping.LocalPort}");

                        if (!await _connection.EnsureConnectedAsync(TimeSpan.FromSeconds(15), _cts.Token))
                        {
                            OnStatusChanged?.Invoke($"UDP stream {streamId} closed: remote session not ready");
                            _udpSessions.TryRemove(streamId, out _);
                            _udpClientSessions.TryRemove(sessionKey, out _);
                            continue;
                        }

                        await SendTunnelFrameToRemoteAsync(TunnelFrame.Open(streamId, mapping.Target));
                    }
                    else
                    {
                        continue;
                    }
                }

                session.LastActivityUtc = DateTime.UtcNow;
                var payloadLength = data.Length;
                var sendBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(payloadLength + 16);
                try
                {
                    TunnelFrame.WriteHeader(sendBuffer, 0, TunnelFrameType.Data, session.StreamId, payloadLength);
                    Buffer.BlockCopy(data, 0, sendBuffer, 16, payloadLength);
                    await SendToRemoteAsync(sendBuffer, 0, 16 + payloadLength);
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(sendBuffer);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception) when (!_isRunning)
            {
                break;
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"UDP receive error on port {mapping.LocalPort}: {ex.Message}");
            }
        }
    }

    private void StartUdpSessionCleaner()
    {
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                var now = DateTime.UtcNow;
                foreach (var session in _udpSessions.Values)
                {
                    if (now - session.LastActivityUtc > TimeSpan.FromSeconds(30))
                    {
                        var streamId = session.StreamId;
                        if (_udpSessions.TryRemove(streamId, out _))
                        {
                            _udpClientSessions.TryRemove((session.LocalPort, session.ClientEndPoint), out _);
                            OnStatusChanged?.Invoke($"UDP virtual session idle timeout: stream {streamId}");
                            _ = CloseRemoteTargetAsync(streamId);
                        }
                    }
                }
            }
        }, _cts.Token);
    }

    private async Task AcceptLocalClientsAsync(TcpListener listener, TunnelMapping mapping)
    {
        while (_isRunning)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync();
                var streamId = NextStreamId();

                _localClients[streamId] = client;
                OnStatusChanged?.Invoke($"Local client connected: stream {streamId}");

                _ = Task.Run(() => HandleLocalClientAsync(streamId, client, mapping));
            }
            catch (Exception) when (!_isRunning)
            {
                break;
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"Accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleLocalClientAsync(uint streamId, TcpClient client, TunnelMapping mapping)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65536 + 16);

        try
        {
            var stream = client.GetStream();
            if (!await _connection.EnsureConnectedAsync(TimeSpan.FromSeconds(15), _cts.Token))
            {
                OnStatusChanged?.Invoke($"Local client stream {streamId} closed: remote session not ready");
                return;
            }

            await SendTunnelFrameToRemoteAsync(TunnelFrame.Open(streamId, mapping.Target));

            while (client.Connected && _isRunning)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(16, buffer.Length - 16), _cts.Token);
                if (bytesRead == 0)
                {
                    break;
                }

                TunnelFrame.WriteHeader(buffer, 0, TunnelFrameType.Data, streamId, bytesRead);
                await SendToRemoteAsync(buffer, 0, 16 + bytesRead);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            OnStatusChanged?.Invoke($"Local client stream {streamId} error: {ex.Message}");
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            _localClients.TryRemove(streamId, out _);
            client.Dispose();
            OnStatusChanged?.Invoke($"Local client disconnected: stream {streamId}");
            await CloseRemoteTargetAsync(streamId);
        }
    }

    private async Task CloseRemoteTargetAsync(uint streamId)
    {
        try
        {
            await SendTunnelFrameToRemoteAsync(TunnelFrame.Close(streamId));
            OnStatusChanged?.Invoke($"Requested remote target close for stream {streamId}");
        }
        catch (Exception ex)
        {
            OnStatusChanged?.Invoke($"Remote target close request failed: {ex.Message}");
        }
    }

    private static uint NextStreamId()
    {
        Span<byte> bytes = stackalloc byte[4];
        Random.Shared.NextBytes(bytes);
        var streamId = BitConverter.ToUInt32(bytes);
        return streamId == 0 ? 1 : streamId;
    }

    private void HandleTunnelData(byte[] data, int length)
    {
        if (!TunnelFrame.TryDecode(data, length, out var frame))
        {
            OnStatusChanged?.Invoke($"Invalid tunnel frame from remote: {length} bytes");
            return;
        }

        if (frame.Type == TunnelFrameType.Data)
        {
            if (_udpSessions.TryGetValue(frame.StreamId, out var udpSession))
            {
                try
                {
                    udpSession.LastActivityUtc = DateTime.UtcNow;
                    udpSession.Listener.Send(frame.Payload.Span, udpSession.ClientEndPoint);
                    OnDataReceived?.Invoke(Array.Empty<byte>(), frame.Payload.Length);
                }
                catch (Exception ex)
                {
                    OnStatusChanged?.Invoke($"Write to local UDP stream {frame.StreamId} ({udpSession.ClientEndPoint}) error: {ex.Message}");
                }
            }
            else if (_localClients.TryGetValue(frame.StreamId, out var client) && client.Connected)
            {
                try
                {
                    var stream = client.GetStream();
                    stream.Write(frame.Payload.Span);
                    stream.Flush();
                    OnDataReceived?.Invoke(Array.Empty<byte>(), frame.Payload.Length);
                }
                catch (Exception ex)
                {
                    OnStatusChanged?.Invoke($"Write to local stream {frame.StreamId} error: {ex.Message}");
                }
            }
        }
        else if (frame.Type == TunnelFrameType.Close)
        {
            if (_udpSessions.TryRemove(frame.StreamId, out var udpSession))
            {
                _udpClientSessions.TryRemove((udpSession.LocalPort, udpSession.ClientEndPoint), out _);
                OnStatusChanged?.Invoke($"UDP virtual session closed by remote: stream {frame.StreamId}");
            }
            else if (_localClients.TryRemove(frame.StreamId, out var closeClient))
            {
                closeClient.Dispose();
            }
        }
        else if (frame.Type == TunnelFrameType.Error)
        {
            OnStatusChanged?.Invoke($"Remote stream {frame.StreamId} error: {frame.PayloadAsString()}");
            if (_udpSessions.TryRemove(frame.StreamId, out var udpSession))
            {
                _udpClientSessions.TryRemove((udpSession.LocalPort, udpSession.ClientEndPoint), out _);
            }
            else if (_localClients.TryRemove(frame.StreamId, out var errorClient))
            {
                errorClient.Dispose();
            }
        }
    }

    private async Task SendTunnelFrameToRemoteAsync(TunnelFrame frame)
    {
        var bytes = frame.Encode();
        await SendToRemoteAsync(bytes, 0, bytes.Length);
    }

    public async Task SendToRemoteAsync(byte[] data, int offset, int length)
    {
        await _connection.SendAsync(data, offset, length);
    }

    private void UpdateState(ConnectionState newState)
    {
        _state = newState;
        OnStatusChanged?.Invoke($"State: {newState}");
    }

    public void Dispose()
    {
        _isRunning = false;
        _cts.Cancel();
        _cts.Dispose();
        _connection.Dispose();

        foreach (var proxy in _localProxies)
        {
            proxy.Stop();
        }

        foreach (var client in _localClients.Values)
        {
            client.Dispose();
        }

        foreach (var proxy in _localUdpProxies)
        {
            try { proxy.Dispose(); } catch { }
        }

        _localClients.Clear();
        _localUdpProxies.Clear();
        _udpSessions.Clear();
        _udpClientSessions.Clear();
    }
}
