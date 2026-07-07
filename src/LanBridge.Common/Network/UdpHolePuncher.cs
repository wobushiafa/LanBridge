using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LanBridge.Common.Kcp;
using LanBridge.Common.Protocol;

namespace LanBridge.Common.Network;

public class UdpHolePuncher : IDisposable
{
    private readonly UdpClient _udpClient;
    private readonly string _localId;
    private IPEndPoint? _remoteEndPoint;
    private readonly ConcurrentDictionary<string, byte> _punchedEndpoints = new();
    private readonly ConcurrentDictionary<uint, KcpSession> _kcpSessions = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<byte[]>> _pendingStunRequests = new();
    private CancellationTokenSource _cts;
    private Task? _receivePumpTask;

    public event Action<IPEndPoint>? OnHolePunched;
    public event Action<IPEndPoint, uint>? OnLanAdvertised;
    public event Action<string>? OnError;
    public event Action<byte[], int, IPEndPoint>? OnUnreliableDataReceived;

    public bool IsPunched => !_punchedEndpoints.IsEmpty;
    public IPEndPoint? LocalEndPoint => _udpClient.Client.LocalEndPoint as IPEndPoint;
    public IPEndPoint? RemoteEndPoint => _remoteEndPoint;
    public UdpClient Client => _udpClient;

    public UdpHolePuncher(int port = 0, string localId = "")
    {
        try
        {
            _udpClient = new UdpClient(AddressFamily.InterNetworkV6);
            _udpClient.Client.DualMode = true;
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        }
        catch
        {
            _udpClient = new UdpClient(port);
        }

        _localId = localId;
        _cts = new CancellationTokenSource();
    }

    public void RegisterStunRequest(byte[] transactionId, TaskCompletionSource<byte[]> tcs)
    {
        var key = Convert.ToHexString(transactionId);
        _pendingStunRequests[key] = tcs;
    }

    public void UnregisterStunRequest(byte[] transactionId)
    {
        var key = Convert.ToHexString(transactionId);
        _pendingStunRequests.TryRemove(key, out _);
    }

    public async Task StartPunchingAsync(IPEndPoint remoteEp, IPEndPoint? remoteEpV6 = null, int intervalMs = 200, int timeoutMs = 10000, bool predictPorts = false)
    {
        _remoteEndPoint = remoteEpV6 ?? remoteEp;
        StartReceivePump();
        var startTime = DateTime.UtcNow;
        var remoteKey = NormalizeEndPoint(remoteEp).ToString();
        var remoteKeyV6 = remoteEpV6 != null ? NormalizeEndPoint(remoteEpV6).ToString() : null;

        while (true)
        {
            if (_punchedEndpoints.ContainsKey(remoteKey))
            {
                break;
            }

            if (remoteKeyV6 != null && _punchedEndpoints.ContainsKey(remoteKeyV6))
            {
                break;
            }

            if ((DateTime.UtcNow - startTime).TotalMilliseconds >= timeoutMs)
            {
                break;
            }

            try
            {
                var punchData = Encoding.UTF8.GetBytes($"PUNCH:{_localId}:{DateTime.UtcNow.Ticks}");

                if (remoteEpV6 != null)
                {
                    try
                    {
                        await _udpClient.SendAsync(punchData, punchData.Length, remoteEpV6);
                        if (predictPorts)
                        {
                            for (int delta = 1; delta <= 8; delta++)
                            {
                                var predictedEp = new IPEndPoint(remoteEpV6.Address, remoteEpV6.Port + delta);
                                await _udpClient.SendAsync(punchData, punchData.Length, predictedEp);
                            }

                            for (int delta = 1; delta <= 3; delta++)
                            {
                                var predictedEp = new IPEndPoint(remoteEpV6.Address, remoteEpV6.Port - delta);
                                await _udpClient.SendAsync(punchData, punchData.Length, predictedEp);
                            }
                        }
                    }
                    catch
                    {
                    }

                    await Task.Delay(30, _cts.Token);
                }

                try
                {
                    await _udpClient.SendAsync(punchData, punchData.Length, remoteEp);
                    if (predictPorts)
                    {
                        for (int delta = 1; delta <= 8; delta++)
                        {
                            var predictedEp = new IPEndPoint(remoteEp.Address, remoteEp.Port + delta);
                            await _udpClient.SendAsync(punchData, punchData.Length, predictedEp);
                        }

                        for (int delta = 1; delta <= 3; delta++)
                        {
                            var predictedEp = new IPEndPoint(remoteEp.Address, remoteEp.Port - delta);
                            await _udpClient.SendAsync(punchData, punchData.Length, predictedEp);
                        }
                    }
                }
                catch (Exception exV4)
                {
                    OnError?.Invoke($"IPv4 punch send error: {exV4.Message}");
                }

                var delay = intervalMs - (remoteEpV6 != null ? 30 : 0);
                if (delay > 0)
                {
                    await Task.Delay(delay, _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Punching error: {ex.Message}");
            }
        }

        if (!_punchedEndpoints.ContainsKey(remoteKey) && (remoteKeyV6 == null || !_punchedEndpoints.ContainsKey(remoteKeyV6)))
        {
            OnError?.Invoke("Hole punching timeout");
        }
    }

    public async Task ListenForPunchAsync(int timeoutMs = 10000)
    {
        StartReceivePump();
        await Task.Delay(timeoutMs, _cts.Token);
    }

    public void RegisterKcpSession(KcpSession session)
    {
        _kcpSessions[session.Conv] = session;
    }

    public void UnregisterKcpSession(uint conv)
    {
        _kcpSessions.TryRemove(conv, out _);
    }

    public void StartReceivePump()
    {
        if (Interlocked.CompareExchange(ref _receivePumpTask, Task.Run(ReceivePumpAsync), null) != null)
        {
            return;
        }
    }

    private async Task ReceivePumpAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(_cts.Token);
                if (TryDispatchStun(result))
                {
                    continue;
                }

                if (result.Buffer.Length >= TunnelFrame.HeaderSize &&
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(result.Buffer.AsSpan(0, 4)) == TunnelFrame.Magic)
                {
                    OnUnreliableDataReceived?.Invoke(result.Buffer, result.Buffer.Length, result.RemoteEndPoint);
                    continue;
                }

                if (TryDispatchKcp(result))
                {
                    continue;
                }

                await TryHandlePunchAsync(result);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"UDP receive pump error: {ex.Message}");
            }
        }
    }

    private bool TryDispatchStun(UdpReceiveResult result)
    {
        if (result.Buffer.Length >= 20)
        {
            var magicCookie = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(result.Buffer.AsSpan(4, 4));
            if (magicCookie == 0x2112A442)
            {
                var transactionId = result.Buffer.AsSpan(8, 12).ToArray();
                var key = Convert.ToHexString(transactionId);
                if (_pendingStunRequests.TryGetValue(key, out var tcs))
                {
                    tcs.TrySetResult(result.Buffer);
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryDispatchKcp(UdpReceiveResult result)
    {
        if (result.Buffer.Length < KcpSegment.HeaderSize)
        {
            return false;
        }

        var conv = BinaryHelper.ReadUInt32(result.Buffer.AsSpan(0, 4));
        if (!_kcpSessions.TryGetValue(conv, out var session))
        {
            return false;
        }

        session.InputPacket(result.Buffer, result.RemoteEndPoint);
        return true;
    }

    private async Task TryHandlePunchAsync(UdpReceiveResult result)
    {
        string message;
        try
        {
            message = Encoding.UTF8.GetString(result.Buffer);
        }
        catch
        {
            return;
        }

        if (message.StartsWith("LB_ADVERTISE:", StringComparison.Ordinal))
        {
            var parts = message.Split(':');
            if (parts.Length == 4 && int.TryParse(parts[2], out var targetPort) && uint.TryParse(parts[3], out var conv))
            {
                var targetEp = new IPEndPoint(result.RemoteEndPoint.Address, targetPort);
                OnLanAdvertised?.Invoke(targetEp, conv);
            }

            return;
        }

        if (message.StartsWith("PUNCH:"))
        {
            _remoteEndPoint = result.RemoteEndPoint;
            MarkPunched(result.RemoteEndPoint);

            var ackData = Encoding.UTF8.GetBytes($"PUNCH_ACK:{_localId}");
            await _udpClient.SendAsync(ackData, ackData.Length, result.RemoteEndPoint);
            return;
        }

        if (message.StartsWith("PUNCH_ACK:"))
        {
            MarkPunched(result.RemoteEndPoint);
        }
    }

    private void MarkPunched(IPEndPoint remoteEndPoint)
    {
        if (_punchedEndpoints.TryAdd(NormalizeEndPoint(remoteEndPoint).ToString(), 0))
        {
            OnHolePunched?.Invoke(remoteEndPoint);
        }
    }

    public void RemovePunchedEndpoint(IPEndPoint remoteEndPoint)
    {
        _punchedEndpoints.TryRemove(NormalizeEndPoint(remoteEndPoint).ToString(), out _);
    }

    private static IPEndPoint NormalizeEndPoint(IPEndPoint endpoint)
    {
        if (endpoint.Address.IsIPv4MappedToIPv6)
        {
            return new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port);
        }

        return endpoint;
    }

    public void TriggerHolePunched(IPEndPoint remoteEndPoint, uint conv)
    {
        _remoteEndPoint = remoteEndPoint;
        MarkPunched(remoteEndPoint);
    }

    public async Task SendAsync(byte[] data)
    {
        var ep = _remoteEndPoint;
        if (ep == null)
        {
            throw new InvalidOperationException("Remote endpoint not set");
        }

        await _udpClient.SendAsync(data, data.Length, ep);
    }

    public async Task<UdpReceiveResult> ReceiveAsync()
    {
        return await _udpClient.ReceiveAsync();
    }

    public async Task<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
    {
        return await _udpClient.ReceiveAsync(cancellationToken);
    }

    public async Task SendAsync(byte[] data, int length, IPEndPoint remoteEndPoint)
    {
        await _udpClient.SendAsync(data, length, remoteEndPoint);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _udpClient.Dispose();
    }
}
