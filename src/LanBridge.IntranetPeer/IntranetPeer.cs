using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LanBridge.Common.Network;
using LanBridge.Common.Protocol;

namespace LanBridge.IntranetPeer;

public class PeerConfig
{
    public string NodeId { get; set; } = "intranet-peer-001";
    public string SignalingServerHost { get; set; } = "127.0.0.1";
    public int SignalingServerPort { get; set; } = 9000;
    public string StunServerHost { get; set; } = "127.0.0.1";
    public int StunServerPort { get; set; } = 9001;
    public int StunAlternateServerPort { get; set; } = 9003;
    public string Token { get; set; } = "default-token";
    public int UdpPort { get; set; }
    public string TargetSourceHost { get; set; } = "127.0.0.1";
    public int TargetSourcePort { get; set; } = 554;
    public bool Verbose { get; set; }
    public bool EnableKcpCongestionControl { get; set; } = false;
    public string LocalBindIp { get; set; } = string.Empty;
    public List<TargetEndpoint> AllowedTargets { get; set; } = new();
    public List<AllowedSubnet> AllowedSubnets { get; set; } = new();
}

public class TargetEndpoint
{
    public string Host { get; set; } = string.Empty;
    public int? Port { get; set; }

    public override string ToString() => Port.HasValue ? $"{Host}:{Port}" : $"{Host}:*";
}

public class AllowedSubnet
{
    public string Cidr { get; set; } = string.Empty;
    public int? Port { get; set; }

    public override string ToString() => Port.HasValue ? $"{Cidr}:{Port}" : $"{Cidr}:*";
}

public class IntranetPeer : IDisposable
{
    private readonly PeerConfig _config;
    private readonly ConnectionNegotiator _connection;
    private readonly ConcurrentDictionary<StreamKey, Task<TargetConnection>> _targetConnections = new();
    private readonly SemaphoreSlim _targetConnectionLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private bool _isRunning;

    public event Action<string>? OnStatusChanged;

    public bool IsConnected => _connection.IsConnected;

    private abstract class TargetConnection : IDisposable
    {
        public abstract bool IsConnected { get; }
        public abstract Task WriteAsync(ReadOnlyMemory<byte> data);
        public abstract void Dispose();
    }

    private sealed class TcpTargetConnection : TargetConnection
    {
        public required TcpClient Client { get; init; }
        public required NetworkStream Stream { get; init; }

        public override bool IsConnected => Client.Connected;

        public override async Task WriteAsync(ReadOnlyMemory<byte> data)
        {
            await Stream.WriteAsync(data);
            await Stream.FlushAsync();
        }

        public override void Dispose()
        {
            try { Stream.Dispose(); } catch { }
            try { Client.Dispose(); } catch { }
        }
    }

    private sealed class UdpTargetConnection : TargetConnection
    {
        private bool _isDisposed;
        public required UdpClient Client { get; init; }
        public required IPEndPoint RemoteEndPoint { get; init; }
        public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;

        public override bool IsConnected => !_isDisposed;

        public override async Task WriteAsync(ReadOnlyMemory<byte> data)
        {
            await Client.SendAsync(data, RemoteEndPoint);
            LastActivityUtc = DateTime.UtcNow;
        }

        public override void Dispose()
        {
            _isDisposed = true;
            try { Client.Dispose(); } catch { }
        }
    }

    public IntranetPeer(PeerConfig config)
    {
        _config = config;
        EnsureDefaultAllowedTarget();

        _connection = new ConnectionNegotiator(new PeerConnectionOptions
        {
            Role = PeerConnectionRole.Intranet,
            NodeId = _config.NodeId,
            SignalingServerHost = _config.SignalingServerHost,
            SignalingServerPort = _config.SignalingServerPort,
            StunServerHost = _config.StunServerHost,
            StunServerPort = _config.StunServerPort,
            StunAlternateServerPort = _config.StunAlternateServerPort,
            Token = _config.Token,
            UdpPort = _config.UdpPort,
            Verbose = _config.Verbose,
            EnableKcpCongestionControl = _config.EnableKcpCongestionControl,
            LocalBindIp = _config.LocalBindIp
        });
        _connection.OnStatusChanged += status => OnStatusChanged?.Invoke(status);
        _connection.OnSessionDataReceived += (sessionId, data, length) => _ = HandleTunnelFrameAsync(sessionId, data, length);
    }

    public async Task StartAsync()
    {
        _isRunning = true;
        OnStatusChanged?.Invoke("Starting...");

        try
        {
            await _connection.StartAsync();
            StartUdpTargetCleaner();

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
            OnStatusChanged?.Invoke($"Error: {ex.Message}");
            throw;
        }
    }

    private readonly record struct StreamKey(string SessionId, uint StreamId);

    private async Task HandleTunnelFrameAsync(string sessionId, byte[] data, int length)
    {
        if (!TunnelFrame.TryDecode(data, length, out var frame))
        {
            OnStatusChanged?.Invoke($"Invalid tunnel frame: {length} bytes");
            return;
        }

        try
        {
            switch (frame.Type)
            {
                case TunnelFrameType.Open:
                    await EnsureTargetConnectionAsync(sessionId, frame.StreamId, GetTargetFromPayload(frame.Payload));
                    break;

                case TunnelFrameType.Data:
                case TunnelFrameType.UnreliableData:
                    await ForwardToTargetAsync(sessionId, frame.StreamId, frame.Payload);
                    break;

                case TunnelFrameType.Close:
                    CloseTargetConnection(sessionId, frame.StreamId);
                    OnStatusChanged?.Invoke($"Closed target source connection for session {sessionId}, stream {frame.StreamId}");
                    break;

                case TunnelFrameType.Ping:
                    await SendTunnelFrameAsync(sessionId, new TunnelFrame { Type = TunnelFrameType.Pong, StreamId = frame.StreamId });
                    break;
            }
        }
        catch (Exception ex)
        {
            OnStatusChanged?.Invoke($"Stream {frame.StreamId} error: {ex.Message}");
            await SendTunnelFrameAsync(sessionId, TunnelFrame.Error(frame.StreamId, ex.Message));
            await SendTunnelFrameAsync(sessionId, TunnelFrame.Close(frame.StreamId));
            CloseTargetConnection(sessionId, streamId: frame.StreamId);
        }
    }

    private async Task ForwardToTargetAsync(string sessionId, uint streamId, ReadOnlyMemory<byte> data)
    {
        try
        {
            var connection = await EnsureTargetConnectionAsync(sessionId, streamId, null);
            await connection.WriteAsync(data);
        }
        catch (Exception ex)
        {
            OnStatusChanged?.Invoke($"Forward error on stream {streamId}: {ex.Message}");
            await SendTunnelFrameAsync(sessionId, TunnelFrame.Error(streamId, ex.Message));
            await SendTunnelFrameAsync(sessionId, TunnelFrame.Close(streamId));
            CloseTargetConnection(sessionId, streamId);
        }
    }

    private async Task<TargetConnection> EnsureTargetConnectionAsync(string sessionId, uint streamId, (string Host, int Port, string Protocol)? requestedTarget)
    {
        var key = new StreamKey(sessionId, streamId);
        if (_targetConnections.TryGetValue(key, out var existingTask))
        {
            try
            {
                var conn = await existingTask;
                if (conn.IsConnected)
                {
                    return conn;
                }
            }
            catch
            {
                _targetConnections.TryRemove(key, out _);
            }
        }

        if (requestedTarget == null)
        {
            throw new InvalidOperationException($"Stream {streamId} is not open.");
        }

        await _targetConnectionLock.WaitAsync(_cts.Token);
        try
        {
            if (_targetConnections.TryGetValue(key, out existingTask))
            {
                try
                {
                    var conn = await existingTask;
                    if (conn.IsConnected)
                    {
                        return conn;
                    }
                }
                catch
                {
                    _targetConnections.TryRemove(key, out _);
                }
            }

            var targetHost = requestedTarget.Value.Host;
            var targetPort = requestedTarget.Value.Port;
            var protocol = requestedTarget.Value.Protocol;
            EnsureTargetAllowed(targetHost, targetPort);

            var connTask = Task.Run<TargetConnection>(async () =>
            {
                if (string.Equals(protocol, "udp", StringComparison.OrdinalIgnoreCase))
                {
                    UdpClient client;
                    IPEndPoint targetEp;
                    try
                    {
                        var addresses = await Dns.GetHostAddressesAsync(targetHost);
                        var ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork || a.AddressFamily == AddressFamily.InterNetworkV6);
                        if (ip == null)
                        {
                            throw new Exception($"Could not resolve host: {targetHost}");
                        }
                        targetEp = new IPEndPoint(ip, targetPort);
                        
                        var localEp = new IPEndPoint(ip.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                        client = new UdpClient(localEp);
                    }
                    catch
                    {
                        throw;
                    }

                    var connection = new UdpTargetConnection
                    {
                        Client = client,
                        RemoteEndPoint = targetEp,
                        LastActivityUtc = DateTime.UtcNow
                    };

                    _ = Task.Run(() => ReadUdpTargetLoopAsync(sessionId, streamId, connection));
                    return connection;
                }
                else
                {
                    var client = new TcpClient();
                    try
                    {
                        await client.ConnectAsync(targetHost, targetPort);
                    }
                    catch
                    {
                        client.Dispose();
                        throw;
                    }

                    var connection = new TcpTargetConnection
                    {
                        Client = client,
                        Stream = client.GetStream()
                    };

                    _ = Task.Run(() => ReadTcpTargetLoopAsync(sessionId, streamId, connection));
                    return connection;
                }
            });

            _targetConnections[key] = connTask;
            return await connTask;
        }
        finally
        {
            _targetConnectionLock.Release();
        }
    }

    private async Task ReadTcpTargetLoopAsync(string sessionId, uint streamId, TcpTargetConnection connection)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65536 + 16);

        try
        {
            while (_isRunning && connection.Client.Connected)
            {
                var bytesRead = await connection.Stream.ReadAsync(buffer.AsMemory(16, buffer.Length - 16), _cts.Token);
                if (bytesRead == 0)
                {
                    break;
                }

                TunnelFrame.WriteHeader(buffer, 0, TunnelFrameType.Data, streamId, bytesRead);
                await SendToExtranetAsync(sessionId, buffer, 0, 16 + bytesRead);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (_targetConnections.ContainsKey(new StreamKey(sessionId, streamId)))
            {
                OnStatusChanged?.Invoke($"TCP target read error on stream {streamId}: {ex.Message}");
                await SendTunnelFrameAsync(sessionId, TunnelFrame.Error(streamId, ex.Message));
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            CloseTargetConnection(sessionId, streamId);
            await SendTunnelFrameAsync(sessionId, TunnelFrame.Close(streamId));
        }
    }

    private async Task ReadUdpTargetLoopAsync(string sessionId, uint streamId, UdpTargetConnection connection)
    {
        while (_isRunning && connection.IsConnected)
        {
            try
            {
                var result = await connection.Client.ReceiveAsync(_cts.Token);
                connection.LastActivityUtc = DateTime.UtcNow;

                var payloadLength = result.Buffer.Length;
                var sendBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(payloadLength + 16);
                try
                {
                    TunnelFrame.WriteHeader(sendBuffer, 0, TunnelFrameType.UnreliableData, streamId, payloadLength);
                    Buffer.BlockCopy(result.Buffer, 0, sendBuffer, 16, payloadLength);
                    await SendUnreliableToExtranetAsync(sessionId, sendBuffer, 0, 16 + payloadLength);
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
            catch (Exception) when (!_isRunning || !connection.IsConnected)
            {
                break;
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"UDP target read error on stream {streamId}: {ex.Message}");
                await SendTunnelFrameAsync(sessionId, TunnelFrame.Error(streamId, ex.Message));
                break;
            }
        }

        CloseTargetConnection(sessionId, streamId);
        await SendTunnelFrameAsync(sessionId, TunnelFrame.Close(streamId));
    }

    private void StartUdpTargetCleaner()
    {
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                var now = DateTime.UtcNow;
                foreach (var kvp in _targetConnections)
                {
                    var task = kvp.Value;
                    if (task.Status == TaskStatus.RanToCompletion)
                    {
                        try
                        {
                            var conn = task.Result;
                            if (conn is UdpTargetConnection udpConn)
                            {
                                if (now - udpConn.LastActivityUtc > TimeSpan.FromSeconds(60))
                                {
                                    var key = kvp.Key;
                                    CloseTargetConnection(key.SessionId, key.StreamId);
                                    OnStatusChanged?.Invoke($"UDP target connection idle timeout: session {key.SessionId}, stream {key.StreamId}");
                                    _ = SendTunnelFrameAsync(key.SessionId, TunnelFrame.Close(key.StreamId));
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
        }, _cts.Token);
    }

    private (string Host, int Port, string Protocol)? GetTargetFromPayload(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length == 0)
        {
            return null;
        }

        var target = Encoding.UTF8.GetString(payload.Span);
        var parts = target.Split(':');
        if (parts.Length < 2)
        {
            throw new InvalidDataException($"Invalid target endpoint: {target}");
        }

        string protocol = "tcp";
        int port;
        string host;

        var lastPart = parts[^1];
        if (string.Equals(lastPart, "udp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(lastPart, "tcp", StringComparison.OrdinalIgnoreCase))
        {
            protocol = lastPart.ToLowerInvariant();
            if (parts.Length < 3 || !int.TryParse(parts[^2], out port))
            {
                throw new InvalidDataException($"Invalid target endpoint: {target}");
            }
            host = string.Join(":", parts[..^2]);
        }
        else
        {
            if (!int.TryParse(lastPart, out port))
            {
                throw new InvalidDataException($"Invalid target endpoint: {target}");
            }
            host = string.Join(":", parts[..^1]);
        }

        return (host, port, protocol);
    }

    private void EnsureDefaultAllowedTarget()
    {
        if (_config.AllowedTargets.Count > 0)
        {
            return;
        }

        _config.AllowedTargets.Add(new TargetEndpoint
        {
            Host = _config.TargetSourceHost,
            Port = _config.TargetSourcePort
        });
    }

    private void EnsureTargetAllowed(string host, int port)
    {
        if (_config.AllowedTargets.Any(target =>
                (!target.Port.HasValue || target.Port == port) &&
                string.Equals(target.Host, host, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (IsAllowedBySubnet(host, port))
        {
            return;
        }

        var allowedTargets = string.Join(", ", _config.AllowedTargets);
        var allowedSubnets = string.Join(", ", _config.AllowedSubnets);
        var allowed = string.Join("; ", new[] { allowedTargets, allowedSubnets }.Where(value => !string.IsNullOrWhiteSpace(value)));
        throw new UnauthorizedAccessException($"Target {host}:{port} is not allowed. Allowed targets: {allowed}");
    }

    private bool IsAllowedBySubnet(string host, int port)
    {
        if (!IPAddress.TryParse(host, out var address))
        {
            return false;
        }

        foreach (var subnet in _config.AllowedSubnets)
        {
            if ((subnet.Port.HasValue && subnet.Port != port) || !IsInCidr(address, subnet.Cidr))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool IsInCidr(IPAddress address, string cidr)
    {
        var parts = cidr.Split('/', 2);
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var network) ||
            !int.TryParse(parts[1], out var prefixLength))
        {
            return false;
        }

        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        if (addressBytes.Length != 4 || networkBytes.Length != 4 || prefixLength < 0 || prefixLength > 32)
        {
            return false;
        }

        var addressValue = BitConverter.ToUInt32(addressBytes.Reverse().ToArray());
        var networkValue = BitConverter.ToUInt32(networkBytes.Reverse().ToArray());
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return (addressValue & mask) == (networkValue & mask);
    }

    private async Task SendTunnelFrameAsync(string sessionId, TunnelFrame frame)
    {
        try
        {
            var bytes = frame.Encode();
            await SendToExtranetAsync(sessionId, bytes, 0, bytes.Length);
        }
        catch (Exception ex)
        {
            if (_config.Verbose)
            {
                OnStatusChanged?.Invoke($"Failed to send frame {frame.Type} for stream {frame.StreamId}: {ex.Message}");
            }
        }
    }

    private void CloseTargetConnection(string sessionId, uint streamId)
    {
        if (!_targetConnections.TryRemove(new StreamKey(sessionId, streamId), out var connTask))
        {
            return;
        }

        _ = connTask.ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion)
            {
                try { t.Result.Dispose(); } catch { }
            }
        });
    }

    private void CloseAllTargetConnections()
    {
        foreach (var streamId in _targetConnections.Keys)
        {
            CloseTargetConnection(streamId.SessionId, streamId.StreamId);
        }
    }

    public async Task SendToExtranetAsync(byte[] data, int offset, int length)
    {
        await _connection.SendAsync(data, offset, length);
    }

    public async Task SendToExtranetAsync(string sessionId, byte[] data, int offset, int length)
    {
        await _connection.SendAsync(sessionId, data, offset, length);
    }

    public async Task SendUnreliableToExtranetAsync(byte[] data, int offset, int length)
    {
        await _connection.SendUnreliableAsync(data, offset, length);
    }

    public async Task SendUnreliableToExtranetAsync(string sessionId, byte[] data, int offset, int length)
    {
        await _connection.SendUnreliableAsync(sessionId, data, offset, length);
    }

    public void Dispose()
    {
        _isRunning = false;
        _cts.Cancel();
        _cts.Dispose();
        _connection.Dispose();
        CloseAllTargetConnections();
        _targetConnectionLock.Dispose();
    }
}
