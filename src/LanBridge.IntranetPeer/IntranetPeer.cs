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
    private readonly ConcurrentDictionary<StreamKey, TargetConnection> _targetConnections = new();
    private readonly SemaphoreSlim _targetConnectionLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private bool _isRunning;

    public event Action<string>? OnStatusChanged;

    public bool IsConnected => _connection.IsConnected;

    private sealed class TargetConnection
    {
        public required TcpClient Client { get; init; }
        public required NetworkStream Stream { get; init; }
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
            Verbose = _config.Verbose
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
                    await ForwardToTargetAsync(sessionId, frame.StreamId, frame.Payload, 0, frame.Payload.Length);
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
            CloseTargetConnection(sessionId, frame.StreamId);
        }
    }

    private async Task ForwardToTargetAsync(string sessionId, uint streamId, byte[] data, int offset, int length)
    {
        try
        {
            var connection = await EnsureTargetConnectionAsync(sessionId, streamId, null);
            await connection.Stream.WriteAsync(data, offset, length);
            await connection.Stream.FlushAsync();
        }
        catch (Exception ex)
        {
            OnStatusChanged?.Invoke($"Forward error on stream {streamId}: {ex.Message}");
            await SendTunnelFrameAsync(sessionId, TunnelFrame.Error(streamId, ex.Message));
            await SendTunnelFrameAsync(sessionId, TunnelFrame.Close(streamId));
            CloseTargetConnection(sessionId, streamId);
        }
    }

    private async Task<TargetConnection> EnsureTargetConnectionAsync(string sessionId, uint streamId, (string Host, int Port)? requestedTarget)
    {
        var key = new StreamKey(sessionId, streamId);
        if (_targetConnections.TryGetValue(key, out var existing) && existing.Client.Connected)
        {
            return existing;
        }

        await _targetConnectionLock.WaitAsync(_cts.Token);
        try
        {
            if (_targetConnections.TryGetValue(key, out existing) && existing.Client.Connected)
            {
                return existing;
            }

            CloseTargetConnection(sessionId, streamId);

            var targetHost = requestedTarget?.Host ?? _config.TargetSourceHost;
            var targetPort = requestedTarget?.Port ?? _config.TargetSourcePort;
            EnsureTargetAllowed(targetHost, targetPort);

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

            var connection = new TargetConnection
            {
                Client = client,
                Stream = client.GetStream()
            };
            _targetConnections[key] = connection;
            OnStatusChanged?.Invoke($"Connected session {sessionId}, stream {streamId} to target source {targetHost}:{targetPort}");

            _ = Task.Run(() => ReadTargetLoopAsync(sessionId, streamId, connection));
            return connection;
        }
        finally
        {
            _targetConnectionLock.Release();
        }
    }

    private async Task ReadTargetLoopAsync(string sessionId, uint streamId, TargetConnection connection)
    {
        var buffer = new byte[65536];

        try
        {
            while (_isRunning && connection.Client.Connected)
            {
                var bytesRead = await connection.Stream.ReadAsync(buffer, 0, buffer.Length, _cts.Token);
                if (bytesRead == 0)
                {
                    break;
                }

                await SendTunnelFrameAsync(sessionId, TunnelFrame.Data(streamId, buffer, 0, bytesRead));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            OnStatusChanged?.Invoke($"Target read error on stream {streamId}: {ex.Message}");
            await SendTunnelFrameAsync(sessionId, TunnelFrame.Error(streamId, ex.Message));
        }
        finally
        {
            CloseTargetConnection(sessionId, streamId);
            await SendTunnelFrameAsync(sessionId, TunnelFrame.Close(streamId));
        }
    }

    private (string Host, int Port)? GetTargetFromPayload(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return null;
        }

        var target = Encoding.UTF8.GetString(payload);
        var separatorIndex = target.LastIndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == target.Length - 1 ||
            !int.TryParse(target[(separatorIndex + 1)..], out var port))
        {
            throw new InvalidDataException($"Invalid target endpoint: {target}");
        }

        return (target[..separatorIndex], port);
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
        var bytes = frame.Encode();
        await SendToExtranetAsync(sessionId, bytes, 0, bytes.Length);
    }

    private void CloseTargetConnection(string sessionId, uint streamId)
    {
        if (!_targetConnections.TryRemove(new StreamKey(sessionId, streamId), out var connection))
        {
            return;
        }

        try { connection.Stream.Dispose(); } catch { }
        try { connection.Client.Dispose(); } catch { }
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
