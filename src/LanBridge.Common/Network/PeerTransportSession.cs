using System.Buffers.Binary;
using LanBridge.Common.Diagnostics;
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
    private readonly object _lock = new();
    private KcpSession? _kcpSession;
    private RelayClient? _relayClient;
    private CancellationTokenSource? _monitorCts;
    private DateTime _lastP2pActivityUtc = DateTime.UtcNow;
    private DateTime _lastMetricsUtc = DateTime.UtcNow;
    private long _lastRttMs = -1;
    private PeerTransportMode _mode;

    // QoS / Rate limiting (optional, null = no limit, zero overhead)
    private TokenBucket? _tokenBucket;
    private FramePriority _priority = FramePriority.Normal;
    private readonly PriorityFrameQueue _priorityQueue = new();
    private readonly SemaphoreSlim _queueDrainLock = new(1, 1);

    public event Action<byte[], int>? OnDataReceived;
    public event Action? OnDisconnected;
    public event Action<string>? OnP2pUnhealthy;
    public event Action<string>? OnStatusChanged;

    public PeerTransportMode Mode
    {
        get { lock (_lock) { return _mode; } }
        private set { lock (_lock) { _mode = value; } }
    }

    public bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return _mode switch
                {
                    PeerTransportMode.P2pDirect => _kcpSession?.IsConnected == true,
                    PeerTransportMode.Relay => _relayClient?.IsConnected == true,
                    _ => false
                };
            }
        }
    }

    public PeerTransportSession(bool verbose)
    {
        _verbose = verbose;
    }

    public void UseP2p(KcpSession session)
    {
        DisposeCurrent();

        lock (_lock)
        {
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

            _mode = PeerTransportMode.P2pDirect;
            _kcpSession.Start();
        }
        StartP2pMonitor();
        OnStatusChanged?.Invoke("P2P connection established");
    }

    public void UseRelay(RelayClient client)
    {
        DisposeCurrent();

        lock (_lock)
        {
            _relayClient = client;
            _relayClient.OnDataReceived += (data, length) => OnDataReceived?.Invoke(data, length);
            _relayClient.OnDisconnected += HandleDisconnected;

            _mode = PeerTransportMode.Relay;
        }
        OnStatusChanged?.Invoke("Relay connection established");
    }

    public async Task SendAsync(byte[] data, int offset, int length)
    {
        // 1. Token bucket rate limiting (zero overhead when not configured)
        if (_tokenBucket != null && !_tokenBucket.TryConsume(length))
        {
            await _tokenBucket.WaitForTokensAsync(length, CancellationToken.None);
        }

        // 2. Priority queue: only enqueue when there's already backlog
        //    (avoids queue overhead when there's no congestion)
        if (!_priorityQueue.IsEmpty)
        {
            _priorityQueue.Enqueue(data, offset, length, _priority);
            await DrainQueueAsync();
            return;
        }

        // 3. Direct send
        await SendCoreAsync(data, offset, length);
    }

    /// <summary>
    /// Set rate limiter for this session. Pass null to disable.
    /// </summary>
    public void SetRateLimit(TokenBucket? bucket)
    {
        _tokenBucket = bucket;
    }

    /// <summary>
    /// Set QoS priority for this session.
    /// </summary>
    public void SetPriority(FramePriority priority)
    {
        _priority = priority;
    }

    /// <summary>
    /// Apply rate limiting for an out-of-band send path (e.g. raw UDP UnreliableData
    /// that bypasses <see cref="SendAsync"/>). Zero overhead when no bucket is configured.
    /// </summary>
    public async Task ApplyRateLimitAsync(int length, CancellationToken ct)
    {
        if (_tokenBucket != null && !_tokenBucket.TryConsume(length))
        {
            await _tokenBucket.WaitForTokensAsync(length, ct);
        }
    }

    private async Task SendCoreAsync(byte[] data, int offset, int length)
    {
        KcpSession? kcp;
        RelayClient? relay;
        PeerTransportMode currentMode;
        lock (_lock)
        {
            kcp = _kcpSession;
            relay = _relayClient;
            currentMode = _mode;
        }

        if (kcp != null && currentMode == PeerTransportMode.P2pDirect && kcp.IsConnected)
        {
            kcp.Send(data, offset, length);
            return;
        }

        if (relay != null && currentMode == PeerTransportMode.Relay && relay.IsConnected)
        {
            await relay.SendAsync(data, offset, length);
        }
    }

    private async Task DrainQueueAsync()
    {
        await _queueDrainLock.WaitAsync();
        try
        {
            while (_priorityQueue.TryDequeue(out var data, out var offset, out var length))
            {
                await SendCoreAsync(data, offset, length);
            }
        }
        finally
        {
            _queueDrainLock.Release();
        }
    }

    private void HandleDisconnected()
    {
        lock (_lock)
        {
            _mode = PeerTransportMode.None;
        }
        OnDisconnected?.Invoke();
    }

    private void HandleP2pData(byte[] data, int length)
    {
        lock (_lock)
        {
            _lastP2pActivityUtc = DateTime.UtcNow;
        }

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
                    var sentTicks = BinaryPrimitives.ReadInt64LittleEndian(frame.Payload.Span);
                    lock (_lock)
                    {
                        _lastRttMs = Math.Max(0, (long)(DateTime.UtcNow - new DateTime(sentTicks, DateTimeKind.Utc)).TotalMilliseconds);
                    }
                }
                return;
            }
        }

        OnDataReceived?.Invoke(data, length);
    }

    private void StartP2pMonitor()
    {
        lock (_lock)
        {
            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
            _monitorCts = new CancellationTokenSource();
        }
        var token = _monitorCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                PeerTransportMode currentMode;
                lock (_lock)
                {
                    currentMode = _mode;
                }
                if (currentMode != PeerTransportMode.P2pDirect)
                {
                    break;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                    lock (_lock)
                    {
                        if (_mode != PeerTransportMode.P2pDirect)
                        {
                            break;
                        }
                    }

                    await SendHeartbeatAsync();
                    MaybeReportStats();

                    DateTime lastActivity;
                    lock (_lock)
                    {
                        lastActivity = _lastP2pActivityUtc;
                    }
                    if (DateTime.UtcNow - lastActivity > TimeSpan.FromSeconds(25))
                    {
                        var reason = $"P2P idle timeout: no KCP data or heartbeat pong for {(DateTime.UtcNow - lastActivity).TotalSeconds:F0}s";
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
        KcpSession? kcp;
        long rttMs;
        lock (_lock)
        {
            if (!_verbose || _kcpSession == null || DateTime.UtcNow - _lastMetricsUtc < TimeSpan.FromSeconds(10))
            {
                return;
            }
            _lastMetricsUtc = DateTime.UtcNow;
            kcp = _kcpSession;
            rttMs = _lastRttMs;
        }

        var stats = kcp.GetStats();
        var lossHint = stats.InputErrors > 0 ? $", inputErrors={stats.InputErrors}" : "";
        var rtt = rttMs >= 0 ? $"{rttMs}ms" : "n/a";
        OnStatusChanged?.Invoke($"KCP stats: rtt={rtt}, mtu={stats.Mtu}, cwnd={stats.Cwnd}, waitSnd={stats.WaitSnd}, sent={stats.SentBytes / 1024.0:F1}KB, recv={stats.ReceivedBytes / 1024.0:F1}KB{lossHint}");
    }

    /// <summary>
    /// Returns a statistics snapshot for TUI dashboard or metrics export.
    /// </summary>
    public PeerSessionStats GetStatsSnapshot()
    {
        lock (_lock)
        {
            var kcpStats = _kcpSession?.GetStats();
            return new PeerSessionStats(
                _mode,
                _lastRttMs,
                kcpStats?.Cwnd ?? 0,
                kcpStats?.WaitSnd ?? 0,
                kcpStats?.SentBytes ?? 0,
                kcpStats?.ReceivedBytes ?? 0,
                kcpStats?.SentPackets ?? 0,
                kcpStats?.ReceivedPackets ?? 0,
                kcpStats?.InputErrors ?? 0,
                _tokenBucket?.Utilization ?? 0.0,
                _tokenBucket?.RateBytesPerSec ?? 0
            );
        }
    }

    private void DisposeCurrent()
    {
        KcpSession? kcp;
        RelayClient? relay;
        CancellationTokenSource? monitor;
        lock (_lock)
        {
            kcp = _kcpSession;
            relay = _relayClient;
            monitor = _monitorCts;
            _kcpSession = null;
            _relayClient = null;
            _monitorCts = null;
            _mode = PeerTransportMode.None;
        }

        monitor?.Cancel();
        monitor?.Dispose();
        kcp?.Dispose();
        relay?.Dispose();
    }

    public void Dispose()
    {
        DisposeCurrent();
        _queueDrainLock.Dispose();
    }
}