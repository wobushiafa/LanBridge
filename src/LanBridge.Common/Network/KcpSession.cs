using System.Net;
using System.Net.Sockets;
using LanBridge.Common.Kcp;

namespace LanBridge.Common.Network;

public sealed record KcpSessionStats(
    uint Conv,
    uint Mtu,
    uint Mss,
    uint Cwnd,
    uint Srtt,
    uint Rttvar,
    uint Rto,
    int WaitSnd,
    long QueuedMessages,
    long SentPackets,
    long SentBytes,
    long ReceivedPackets,
    long ReceivedBytes,
    long DeliveredMessages,
    long DeliveredBytes,
    long InputErrors,
    DateTime LastReceivedUtc);

public class KcpSession : IDisposable
{
    private readonly Kcp.Kcp _kcp;
    private readonly UdpClient _udpClient;
    private readonly object _kcpLock = new();
    private readonly CancellationTokenSource _cts;
    private readonly bool _ownReceiveLoop;
    private volatile bool _isRunning;
    private long _queuedMessages;
    private long _sentPackets;
    private long _sentBytes;
    private long _receivedPackets;
    private long _receivedBytes;
    private long _deliveredMessages;
    private long _deliveredBytes;
    private long _inputErrors;
    private DateTime _lastReceivedUtc = DateTime.UtcNow;

    public event Action<byte[], int>? OnDataReceived;
    public event Action? OnDisconnected;
    public event Action<string>? OnTrace;

    public uint Conv { get; }
    public bool IsConnected => _kcp.State == 0;

    public KcpSession(uint conv, UdpClient udpClient, IPEndPoint remoteEp, int mtu = 1200, bool enableCongestionControl = true, bool ownReceiveLoop = true)
    {
        Conv = conv;
        _udpClient = udpClient;
        _cts = new CancellationTokenSource();
        _ownReceiveLoop = ownReceiveLoop;

        _kcp = new Kcp.Kcp(conv, (data, size, ep) =>
        {
            try
            {
                _udpClient.Send(data, size, ep);
                var sentPackets = Interlocked.Increment(ref _sentPackets);
                Interlocked.Add(ref _sentBytes, size);
                if (sentPackets <= 5)
                {
                    OnTrace?.Invoke($"KCP UDP sent {size} bytes to {ep}");
                }
            }
            catch (Exception ex)
            {
                OnTrace?.Invoke($"KCP UDP send failed: {ex.Message}");
            }
        }, remoteEp);

        _kcp.SetMtu((uint)mtu);
        _kcp.SetNodelay(1, 10, 2, enableCongestionControl ? 0 : 1);
        _kcp.WndSize(1024, 1024);
        _kcp.UseAdaptiveCongestion = true;
    }

    public bool UseAdaptiveCongestion
    {
        get
        {
            lock (_kcpLock)
            {
                return _kcp.UseAdaptiveCongestion;
            }
        }
        set
        {
            lock (_kcpLock)
            {
                _kcp.UseAdaptiveCongestion = value;
            }
        }
    }

    public void Start()
    {
        _isRunning = true;
        _ = Task.Run(UpdateLoopAsync);
        if (_ownReceiveLoop)
        {
            _ = Task.Run(ReceiveLoopAsync);
        }
    }

    public int Send(byte[] data, int offset, int length)
    {
        int result;
        int waitSnd;
        lock (_kcpLock)
        {
            result = _kcp.Send(data, offset, length);
            waitSnd = _kcp.WaitSnd();
        }

        var queuedMessages = Interlocked.Increment(ref _queuedMessages);
        if (queuedMessages <= 5)
        {
            OnTrace?.Invoke($"KCP queued {length} bytes, result={result}, waitSnd={waitSnd}, conv={Conv}");
        }

        return result;
    }

    public KcpSessionStats GetStats()
    {
        lock (_kcpLock)
        {
            return new KcpSessionStats(
                Conv,
                _kcp.Mtu,
                _kcp.Mss,
                _kcp.Cwnd,
                _kcp.Srtt,
                _kcp.Rttvar,
                _kcp.Rto,
                _kcp.WaitSnd(),
                Interlocked.Read(ref _queuedMessages),
                Interlocked.Read(ref _sentPackets),
                Interlocked.Read(ref _sentBytes),
                Interlocked.Read(ref _receivedPackets),
                Interlocked.Read(ref _receivedBytes),
                Interlocked.Read(ref _deliveredMessages),
                Interlocked.Read(ref _deliveredBytes),
                Interlocked.Read(ref _inputErrors),
                _lastReceivedUtc);
        }
    }

    public int Receive(byte[] buffer, int offset, int length)
    {
        lock (_kcpLock)
        {
            return _kcp.Receive(buffer, offset, length);
        }
    }

    private async Task UpdateLoopAsync()
    {
        while (_isRunning)
        {
            try
            {
                lock (_kcpLock)
                {
                    _kcp.Update((uint)Environment.TickCount);
                }

                await Task.Delay(10, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                OnTrace?.Invoke($"KCP update loop error: {ex.Message}");
            }
        }
    }

    private async Task ReceiveLoopAsync()
    {
        while (_isRunning)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync();
                InputPacket(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                OnTrace?.Invoke($"KCP receive loop error: {ex.Message}");
            }
        }
    }

    public void InputPacket(byte[] packet, IPEndPoint remoteEndPoint)
    {
        Interlocked.Increment(ref _receivedPackets);
        Interlocked.Add(ref _receivedBytes, packet.Length);
        _lastReceivedUtc = DateTime.UtcNow;

        int inputResult;
        int size;
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            lock (_kcpLock)
            {
                inputResult = _kcp.Input(packet, 0, packet.Length);
                size = _kcp.Receive(buffer, 0, buffer.Length);
            }

            if (inputResult < 0 && packet.Length >= 24)
            {
                Interlocked.Increment(ref _inputErrors);
                OnTrace?.Invoke($"KCP input ignored {packet.Length} bytes from {remoteEndPoint}, result={inputResult}");
            }

            if (size > 0)
            {
                var deliveredMessages = Interlocked.Increment(ref _deliveredMessages);
                Interlocked.Add(ref _deliveredBytes, size);
                if (deliveredMessages <= 5)
                {
                    OnTrace?.Invoke($"KCP delivered {size} bytes from {remoteEndPoint}");
                }

                OnDataReceived?.Invoke(buffer, size);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        _isRunning = false;
        _cts.Cancel();
        _cts.Dispose();
        _kcp.Dispose();
        OnDisconnected?.Invoke();
    }
}
