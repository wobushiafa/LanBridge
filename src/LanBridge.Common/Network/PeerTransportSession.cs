using System.Buffers.Binary;
using LanBridge.Common.Protocol;

namespace LanBridge.Common.Network;

public enum PeerTransportMode
{
    None,
    P2pDirect,
    Relay
}

public sealed class PeerTransportSession : IDisposable
{
    private readonly bool _verbose;
    private KcpSession? _kcpSession;
    private RelayClient? _relayClient;
    private CancellationTokenSource? _monitorCts;
    private DateTime _lastP2pActivityUtc = DateTime.UtcNow;
    private DateTime _lastMetricsUtc = DateTime.UtcNow;
    private long _lastRttMs = -1;

    public event Action<byte[], int>? OnDataReceived;
    public event Action? OnDisconnected;
    public event Action<string>? OnP2pUnhealthy;
    public event Action<string>? OnStatusChanged;

    public PeerTransportMode Mode { get; private set; } = PeerTransportMode.None;

    public bool IsConnected => Mode switch
    {
        PeerTransportMode.P2pDirect => _kcpSession?.IsConnected == true,
        PeerTransportMode.Relay => _relayClient?.IsConnected == true,
        _ => false
    };

    public PeerTransportSession(bool verbose)
    {
        _verbose = verbose;
    }

    public void UseP2p(KcpSession session)
    {
        DisposeCurrent();

        _kcpSession = session;
        _lastP2pActivityUtc = DateTime.UtcNow;
        _lastMetricsUtc = DateTime.UtcNow;
        _lastRttMs = -1;
        _kcpSession.OnDataReceived += HandleP2pData;
        _kcpSession.OnDisconnected += HandleDisconnected;
        if (_verbose)
        {
            _kcpSession.OnTrace += message => OnStatusChanged?.Invoke(message);
        }

        Mode = PeerTransportMode.P2pDirect;
        _kcpSession.Start();
        StartP2pMonitor();
        OnStatusChanged?.Invoke("P2P connection established");
    }

    public void UseRelay(RelayClient client)
    {
        DisposeCurrent();

        _relayClient = client;
        _relayClient.OnDataReceived += (data, length) => OnDataReceived?.Invoke(data, length);
        _relayClient.OnDisconnected += HandleDisconnected;

        Mode = PeerTransportMode.Relay;
        OnStatusChanged?.Invoke("Relay connection established");
    }

    public async Task SendAsync(byte[] data, int offset, int length)
    {
        if (_kcpSession != null && Mode == PeerTransportMode.P2pDirect && _kcpSession.IsConnected)
        {
            _kcpSession.Send(data, offset, length);
            return;
        }

        if (_relayClient != null && Mode == PeerTransportMode.Relay && _relayClient.IsConnected)
        {
            await _relayClient.SendAsync(data, offset, length);
        }
    }

    private void HandleDisconnected()
    {
        Mode = PeerTransportMode.None;
        OnDisconnected?.Invoke();
    }

    private void HandleP2pData(byte[] data, int length)
    {
        _lastP2pActivityUtc = DateTime.UtcNow;

        if (TunnelFrame.TryDecode(data, length, out var frame) && frame.StreamId == 0)
        {
            if (frame.Type == TunnelFrameType.Ping)
            {
                var response = TunnelFrame.Pong(frame.Payload).Encode();
                _ = SendAsync(response, 0, response.Length);
                return;
            }

            if (frame.Type == TunnelFrameType.Pong)
            {
                if (frame.Payload.Length == 8)
                {
                    var sentTicks = BinaryPrimitives.ReadInt64LittleEndian(frame.Payload);
                    _lastRttMs = Math.Max(0, (long)(DateTime.UtcNow - new DateTime(sentTicks, DateTimeKind.Utc)).TotalMilliseconds);
                }
                return;
            }
        }

        OnDataReceived?.Invoke(data, length);
    }

    private void StartP2pMonitor()
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = new CancellationTokenSource();
        var token = _monitorCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && Mode == PeerTransportMode.P2pDirect)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                    if (token.IsCancellationRequested || Mode != PeerTransportMode.P2pDirect)
                    {
                        break;
                    }

                    await SendHeartbeatAsync();
                    MaybeReportStats();

                    if (DateTime.UtcNow - _lastP2pActivityUtc > TimeSpan.FromSeconds(25))
                    {
                        var reason = $"P2P idle timeout: no KCP data or heartbeat pong for {(DateTime.UtcNow - _lastP2pActivityUtc).TotalSeconds:F0}s";
                        OnStatusChanged?.Invoke(reason);
                        OnP2pUnhealthy?.Invoke(reason);
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    OnStatusChanged?.Invoke($"P2P monitor error: {ex.Message}");
                }
            }
        }, token);
    }

    private async Task SendHeartbeatAsync()
    {
        Span<byte> payload = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(payload, DateTime.UtcNow.Ticks);
        var frame = TunnelFrame.Ping(payload.ToArray()).Encode();
        await SendAsync(frame, 0, frame.Length);
    }

    private void MaybeReportStats()
    {
        if (!_verbose || _kcpSession == null || DateTime.UtcNow - _lastMetricsUtc < TimeSpan.FromSeconds(10))
        {
            return;
        }

        _lastMetricsUtc = DateTime.UtcNow;
        var stats = _kcpSession.GetStats();
        var lossHint = stats.InputErrors > 0 ? $", inputErrors={stats.InputErrors}" : "";
        var rtt = _lastRttMs >= 0 ? $"{_lastRttMs}ms" : "n/a";
        OnStatusChanged?.Invoke($"KCP stats: rtt={rtt}, mtu={stats.Mtu}, cwnd={stats.Cwnd}, waitSnd={stats.WaitSnd}, sent={stats.SentBytes / 1024.0:F1}KB, recv={stats.ReceivedBytes / 1024.0:F1}KB{lossHint}");
    }

    private void DisposeCurrent()
    {
        _kcpSession?.Dispose();
        _relayClient?.Dispose();
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = null;
        _kcpSession = null;
        _relayClient = null;
        Mode = PeerTransportMode.None;
    }

    public void Dispose()
    {
        DisposeCurrent();
    }
}
