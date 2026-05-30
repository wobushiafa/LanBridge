using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LanBridge.Common.Kcp;

namespace LanBridge.Common.Network;

/// <summary>
/// UDP 打洞管理器
/// </summary>
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
    public event Action<string>? OnError;
    
    public bool IsPunched => !_punchedEndpoints.IsEmpty;
    public IPEndPoint? LocalEndPoint => _udpClient.Client.LocalEndPoint as IPEndPoint;
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
    
    /// <summary>
    /// 开始打洞（主动方）
    /// </summary>
    public async Task StartPunchingAsync(IPEndPoint remoteEp, IPEndPoint? remoteEpV6 = null, int intervalMs = 200, int timeoutMs = 10000)
    {
        _remoteEndPoint = remoteEpV6 ?? remoteEp;
        StartReceivePump();
        var startTime = DateTime.UtcNow;
        var remoteKey = remoteEp.ToString();
        var remoteKeyV6 = remoteEpV6?.ToString();

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
                // 发送打洞包
                var punchData = Encoding.UTF8.GetBytes($"PUNCH:{_localId}:{DateTime.UtcNow.Ticks}");
                
                if (remoteEpV6 != null)
                {
                    await _udpClient.SendAsync(punchData, punchData.Length, remoteEpV6);
                    // 给 IPv6 分配 30ms 的打洞领先权 (Happy Eyeballs 延迟偏好)
                    await Task.Delay(30, _cts.Token);
                }
                
                await _udpClient.SendAsync(punchData, punchData.Length, remoteEp);
                
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
    
    /// <summary>
    /// 监听打洞包（被动方）
    /// </summary>
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
        if (_receivePumpTask != null)
        {
            return;
        }

        _receivePumpTask = Task.Run(ReceivePumpAsync);
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
        if (_punchedEndpoints.TryAdd(remoteEndPoint.ToString(), 0))
        {
            OnHolePunched?.Invoke(remoteEndPoint);
        }
    }

    public void RemovePunchedEndpoint(IPEndPoint remoteEndPoint)
    {
        _punchedEndpoints.TryRemove(remoteEndPoint.ToString(), out _);
    }
    
    /// <summary>
    /// 发送数据
    /// </summary>
    public async Task SendAsync(byte[] data)
    {
        if (_remoteEndPoint == null)
            throw new InvalidOperationException("Remote endpoint not set");
        
        await _udpClient.SendAsync(data, data.Length, _remoteEndPoint);
    }
    
    /// <summary>
    /// 接收数据
    /// </summary>
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
        _cts?.Cancel();
        _cts?.Dispose();
        _udpClient?.Dispose();
    }
}

/// <summary>
/// TCP 信令客户端
/// </summary>
public class SignalingClient : IDisposable
{
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private readonly string _serverHost;
    private readonly int _serverPort;
    private CancellationTokenSource _cts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _isConnected;
    
    public event Action<string>? OnMessageReceived;
    public event Action? OnDisconnected;
    public event Action<string>? OnError;
    
    public bool IsConnected => _isConnected;
    
    public SignalingClient(string host, int port)
    {
        _serverHost = host;
        _serverPort = port;
        _cts = new CancellationTokenSource();
    }
    
    /// <summary>
    /// 连接到信令服务器
    /// </summary>
    public async Task ConnectAsync()
    {
        try
        {
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(_serverHost, _serverPort);
            _stream = _tcpClient.GetStream();
            _isConnected = true;
            
            // 开始接收消息
            _ = Task.Run(ReceiveLoopAsync);
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Connect error: {ex.Message}");
            throw;
        }
    }
    
    /// <summary>
    /// 发送消息
    /// </summary>
    public async Task SendAsync(string message)
    {
        if (!_isConnected || _stream == null)
            throw new InvalidOperationException("Not connected");
        
        await _sendLock.WaitAsync(_cts.Token);
        try
        {
            var data = Encoding.UTF8.GetBytes(message);
            var lengthBytes = BitConverter.GetBytes(data.Length);

            await _stream.WriteAsync(lengthBytes, 0, 4);
            await _stream.WriteAsync(data, 0, data.Length);
            await _stream.FlushAsync();
        }
        finally
        {
            _sendLock.Release();
        }
    }
    
    /// <summary>
    /// 接收循环
    /// </summary>
    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[65536];
        
        try
        {
            while (_isConnected && _stream != null)
            {
                // 读取消息长度
                var lengthBuffer = new byte[4];
                int bytesRead = 0;
                while (bytesRead < 4)
                {
                    int read = await _stream.ReadAsync(lengthBuffer, bytesRead, 4 - bytesRead);
                    if (read == 0)
                        throw new IOException("Connection closed");
                    bytesRead += read;
                }
                
                int messageLength = BitConverter.ToInt32(lengthBuffer, 0);
                if (messageLength <= 0 || messageLength > buffer.Length)
                    throw new InvalidDataException($"Invalid message length: {messageLength}");
                
                // 读取消息内容
                bytesRead = 0;
                while (bytesRead < messageLength)
                {
                    int read = await _stream.ReadAsync(buffer, bytesRead, messageLength - bytesRead);
                    if (read == 0)
                        throw new IOException("Connection closed");
                    bytesRead += read;
                }
                
                var message = Encoding.UTF8.GetString(buffer, 0, messageLength);
                OnMessageReceived?.Invoke(message);
            }
        }
        catch (Exception ex)
        {
            if (_isConnected)
            {
                OnError?.Invoke($"Receive error: {ex.Message}");
                _isConnected = false;
                OnDisconnected?.Invoke();
            }
        }
    }
    
    public void Dispose()
    {
        _isConnected = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _sendLock.Dispose();
        _stream?.Dispose();
        _tcpClient?.Dispose();
    }
}

/// <summary>
/// TCP 信令服务器
/// </summary>
public class SignalingServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Dictionary<string, TcpClient> _clients = new();
    private readonly Dictionary<string, NetworkStream> _streams = new();
    private CancellationTokenSource _cts;
    private bool _isRunning;
    
    public event Action<string, string>? OnClientMessage;
    public event Action<string>? OnClientConnected;
    public event Action<string>? OnClientDisconnected;
    
    public SignalingServer(int port)
    {
        try
        {
            _listener = new TcpListener(IPAddress.IPv6Any, port);
            _listener.Server.DualMode = true;
        }
        catch
        {
            _listener = new TcpListener(IPAddress.Any, port);
        }
        _cts = new CancellationTokenSource();
    }
    
    /// <summary>
    /// 启动服务器
    /// </summary>
    public async Task StartAsync()
    {
        _listener.Start();
        _isRunning = true;
        
        while (_isRunning)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync();
                var clientId = Guid.NewGuid().ToString("N")[..8];
                
                _clients[clientId] = client;
                _streams[clientId] = client.GetStream();
                
                OnClientConnected?.Invoke(clientId);
                
                _ = Task.Run(() => HandleClientAsync(clientId, client));
            }
            catch (Exception) when (!_isRunning)
            {
                // Server stopped
            }
            catch (Exception)
            {
                // Log error
            }
        }
    }
    
    /// <summary>
    /// 处理客户端连接
    /// </summary>
    private async Task HandleClientAsync(string clientId, TcpClient client)
    {
        var buffer = new byte[65536];
        
        try
        {
            var stream = client.GetStream();
            
            while (client.Connected)
            {
                // 读取消息长度
                var lengthBuffer = new byte[4];
                int bytesRead = 0;
                while (bytesRead < 4)
                {
                    int read = await stream.ReadAsync(lengthBuffer, bytesRead, 4 - bytesRead);
                    if (read == 0)
                        throw new IOException("Connection closed");
                    bytesRead += read;
                }
                
                int messageLength = BitConverter.ToInt32(lengthBuffer, 0);
                if (messageLength <= 0 || messageLength > buffer.Length)
                    throw new InvalidDataException($"Invalid message length: {messageLength}");
                
                // 读取消息内容
                bytesRead = 0;
                while (bytesRead < messageLength)
                {
                    int read = await stream.ReadAsync(buffer, bytesRead, messageLength - bytesRead);
                    if (read == 0)
                        throw new IOException("Connection closed");
                    bytesRead += read;
                }
                
                var message = Encoding.UTF8.GetString(buffer, 0, messageLength);
                OnClientMessage?.Invoke(clientId, message);
            }
        }
        catch (Exception)
        {
            // Client disconnected
        }
        finally
        {
            _clients.Remove(clientId);
            _streams.Remove(clientId);
            client.Dispose();
            OnClientDisconnected?.Invoke(clientId);
        }
    }
    
    /// <summary>
    /// 发送消息给指定客户端
    /// </summary>
    public async Task SendToClientAsync(string clientId, string message)
    {
        if (!_streams.TryGetValue(clientId, out var stream))
            throw new ArgumentException($"Client {clientId} not found");
        
        var data = Encoding.UTF8.GetBytes(message);
        var lengthBytes = BitConverter.GetBytes(data.Length);
        
        await stream.WriteAsync(lengthBytes, 0, 4);
        await stream.WriteAsync(data, 0, data.Length);
        await stream.FlushAsync();
    }
    
    /// <summary>
    /// 移除客户端
    /// </summary>
    public void RemoveClient(string clientId)
    {
        if (_clients.TryGetValue(clientId, out var client))
        {
            client.Dispose();
            _clients.Remove(clientId);
            _streams.Remove(clientId);
        }
    }
    
    public void Dispose()
    {
        _isRunning = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _listener.Stop();
        
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }
        
        _clients.Clear();
        _streams.Clear();
    }
}

/// <summary>
/// KCP 会话管理器
/// </summary>
public sealed record KcpSessionStats(
    uint Conv,
    uint Mtu,
    uint Mss,
    uint Cwnd,
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
    private IPEndPoint? _remoteEndPoint;
    private readonly CancellationTokenSource _cts;
    private readonly bool _ownReceiveLoop;
    private bool _isRunning;
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
        _remoteEndPoint = remoteEp;
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
        
        // 设置无延迟模式与窗口大小
        _kcp.SetMtu((uint)mtu);
        _kcp.SetNodelay(1, 10, 2, enableCongestionControl ? 0 : 1);
        _kcp.WndSize(1024, 1024);
    }
    
    /// <summary>
    /// 启动会话
    /// </summary>
    public void Start()
    {
        _isRunning = true;
        _ = Task.Run(UpdateLoopAsync);
        if (_ownReceiveLoop)
        {
            _ = Task.Run(ReceiveLoopAsync);
        }
    }
    
    /// <summary>
    /// 发送数据
    /// </summary>
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
    
    /// <summary>
    /// 接收数据
    /// </summary>
    public int Receive(byte[] buffer, int offset, int length)
    {
        lock (_kcpLock)
        {
            return _kcp.Receive(buffer, offset, length);
        }
    }
    
    /// <summary>
    /// 更新循环
    /// </summary>
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
            catch { }
        }
    }
    
    /// <summary>
    /// 接收循环
    /// </summary>
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
            catch { }
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
        _cts?.Cancel();
        _cts?.Dispose();
        _kcp?.Dispose();
        OnDisconnected?.Invoke();
    }
}

/// <summary>
/// TCP 中转客户端
/// </summary>
public class RelayClient : IDisposable
{
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _isConnected;
    
    public event Action<byte[], int>? OnDataReceived;
    public event Action? OnDisconnected;
    
    public bool IsConnected => _isConnected;
    
    public RelayClient()
    {
        _cts = new CancellationTokenSource();
    }
    
    /// <summary>
    /// 连接到中转服务器
    /// </summary>
    public async Task ConnectAsync(string host, int port)
    {
        _tcpClient = new TcpClient();
        await _tcpClient.ConnectAsync(host, port);
        _stream = _tcpClient.GetStream();
        _isConnected = true;
        
        _ = Task.Run(ReceiveLoopAsync);
    }
    
    /// <summary>
    /// 发送数据
    /// </summary>
    public async Task SendAsync(byte[] data, int offset, int length)
    {
        if (!_isConnected || _stream == null)
            throw new InvalidOperationException("Not connected");
        
        await _sendLock.WaitAsync(_cts.Token);
        try
        {
            var lengthBytes = BitConverter.GetBytes(length);
            await _stream.WriteAsync(lengthBytes, 0, 4);
            await _stream.WriteAsync(data, offset, length);
            await _stream.FlushAsync();
        }
        finally
        {
            _sendLock.Release();
        }
    }
    
    /// <summary>
    /// 接收循环
    /// </summary>
    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[65536];
        
        try
        {
            while (_isConnected && _stream != null)
            {
                var lengthBuffer = new byte[4];
                int bytesRead = 0;
                while (bytesRead < 4)
                {
                    int read = await _stream.ReadAsync(lengthBuffer, bytesRead, 4 - bytesRead);
                    if (read == 0)
                        throw new IOException("Connection closed");
                    bytesRead += read;
                }
                
                int messageLength = BitConverter.ToInt32(lengthBuffer, 0);
                if (messageLength <= 0 || messageLength > buffer.Length)
                    throw new InvalidDataException($"Invalid message length: {messageLength}");
                
                bytesRead = 0;
                while (bytesRead < messageLength)
                {
                    int read = await _stream.ReadAsync(buffer, bytesRead, messageLength - bytesRead);
                    if (read == 0)
                        throw new IOException("Connection closed");
                    bytesRead += read;
                }
                
                var payload = buffer.AsSpan(0, messageLength).ToArray();
                OnDataReceived?.Invoke(payload, payload.Length);
            }
        }
        catch (Exception)
        {
            if (_isConnected)
            {
                _isConnected = false;
                OnDisconnected?.Invoke();
            }
        }
    }
    
    public void Dispose()
    {
        _isConnected = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _sendLock.Dispose();
        _stream?.Dispose();
        _tcpClient?.Dispose();
    }
}
