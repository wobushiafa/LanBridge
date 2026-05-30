using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LanBridge.Common.Protocol;
using LanBridge.Common.Network;

namespace LanBridge.SignalingServer;

/// <summary>
/// 信令服务器配置
/// </summary>
public class ServerConfig
{
    public int SignalingPort { get; set; } = 9000;
    public int StunPort { get; set; } = 9001;
    public int StunAlternatePort { get; set; } = 9003;
    public int RelayPort { get; set; } = 9002;
    public int MaxRelaySessions { get; set; } = 100;
    public int RelayTimeoutMs { get; set; } = 30000;
}

/// <summary>
/// 注册的节点信息
/// </summary>
public class NodeInfo
{
    public string NodeId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public IPEndPoint? PublicEndPoint { get; set; }
    public IPEndPoint? PublicEndPointV6 { get; set; }
    public DateTime LastSeen { get; set; }
    public bool IsIntranet { get; set; }
    public StunNatType NatType { get; set; } = StunNatType.Unknown;
}

/// <summary>
/// 中转会话
/// </summary>
public class RelaySession
{
    public string SessionId { get; set; } = string.Empty;
    public string IntranetClientId { get; set; } = string.Empty;
    public string ExtranetClientId { get; set; } = string.Empty;
    public TcpClient? IntranetClient { get; set; }
    public TcpClient? ExtranetClient { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>
/// STUN 服务
/// </summary>
public class StunService : IDisposable
{
    private readonly UdpClient _primaryClient;
    private readonly UdpClient? _alternateClient;
    private readonly int _primaryPort;
    private readonly int _alternatePort;
    private CancellationTokenSource _cts;
    private bool _isRunning;
    
    public event Action<string, IPEndPoint>? OnStunRequest;
    
    private static UdpClient CreateDualStackUdpClient(int port)
    {
        try
        {
            var client = new UdpClient(AddressFamily.InterNetworkV6);
            client.Client.DualMode = true;
            client.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            return client;
        }
        catch
        {
            return new UdpClient(port);
        }
    }
    
    public StunService(int primaryPort, int alternatePort = 0)
    {
        _primaryPort = primaryPort;
        _alternatePort = alternatePort;
        _primaryClient = CreateDualStackUdpClient(primaryPort);
        if (alternatePort > 0 && alternatePort != primaryPort)
        {
            _alternateClient = CreateDualStackUdpClient(alternatePort);
        }
        _cts = new CancellationTokenSource();
    }
    
    public async Task StartAsync()
    {
        _isRunning = true;
        Console.WriteLine($"[STUN] Listening on UDP port {_primaryPort} (dual-stack)");
        if (_alternateClient != null)
        {
            Console.WriteLine($"[STUN] Alternate listening on UDP port {_alternatePort} (dual-stack)");
        }

        var primaryLoop = ReceiveLoopAsync(_primaryClient, _alternateClient);
        var alternateLoop = _alternateClient == null
            ? Task.CompletedTask
            : ReceiveLoopAsync(_alternateClient, _primaryClient);

        await Task.WhenAll(primaryLoop, alternateLoop);
    }

    private async Task ReceiveLoopAsync(UdpClient receiveClient, UdpClient? changePortClient)
    {
        while (_isRunning)
        {
            try
            {
                var result = await receiveClient.ReceiveAsync();
                _ = Task.Run(() => HandleStunRequestAsync(result, receiveClient, changePortClient));
            }
            catch (Exception) when (!_isRunning)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[STUN] Error: {ex.Message}");
            }
        }
    }
    
    private async Task HandleStunRequestAsync(UdpReceiveResult result, UdpClient receiveClient, UdpClient? changePortClient)
    {
        try
        {
            if (StunProtocol.TryParseBindingRequest(result.Buffer, out var standardRequest))
            {
                var responseClient = standardRequest.ChangePort && changePortClient != null
                    ? changePortClient
                    : receiveClient;
                var response = StunProtocol.CreateBindingSuccessResponse(standardRequest.TransactionId, result.RemoteEndPoint);
                await responseClient.SendAsync(response, response.Length, result.RemoteEndPoint);
                Console.WriteLine($"[STUN] Binding response to {result.RemoteEndPoint}" +
                                  (standardRequest.ChangePort && changePortClient != null ? " from alternate port" : ""));
                OnStunRequest?.Invoke("STUN_BINDING_REQUEST", result.RemoteEndPoint);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[STUN] Handle error: {ex.Message}");
        }
    }
    
    public void Dispose()
    {
        _isRunning = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _primaryClient?.Dispose();
        _alternateClient?.Dispose();
    }
}

/// <summary>
/// 信令服务
/// </summary>
public class SignalingService : IDisposable
{
    private readonly TcpListener _listener;
    private readonly int _port;
    private readonly int _relayPort;
    private readonly ConcurrentDictionary<string, TcpClient> _clients = new();
    private readonly ConcurrentDictionary<string, NetworkStream> _streams = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sendLocks = new();
    private readonly ConcurrentDictionary<string, NodeInfo> _nodes = new();
    private readonly ConcurrentDictionary<string, string> _clientToNode = new();
    private CancellationTokenSource _cts;
    private bool _isRunning;
    
    public event Action<string, BaseMessage>? OnMessageReceived;
    
    public SignalingService(int port, int relayPort)
    {
        _port = port;
        _relayPort = relayPort;
        _listener = new TcpListener(IPAddress.Any, port);
        _cts = new CancellationTokenSource();
    }
    
    public async Task StartAsync()
    {
        _listener.Start();
        _isRunning = true;
        Console.WriteLine($"[Signaling] Listening on TCP port {_port}");
        
        while (_isRunning)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync();
                var clientId = Guid.NewGuid().ToString("N")[..8];
                
                _clients[clientId] = client;
                _streams[clientId] = client.GetStream();
                _sendLocks[clientId] = new SemaphoreSlim(1, 1);
                
                Console.WriteLine($"[Signaling] Client connected: {clientId}");
                _ = Task.Run(() => HandleClientAsync(clientId, client));
            }
            catch (Exception) when (!_isRunning)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Signaling] Accept error: {ex.Message}");
            }
        }
    }
    
    private async Task HandleClientAsync(string clientId, TcpClient client)
    {
        var buffer = new byte[65536];
        
        try
        {
            var stream = client.GetStream();
            
            while (client.Connected && _isRunning)
            {
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
                
                bytesRead = 0;
                while (bytesRead < messageLength)
                {
                    int read = await stream.ReadAsync(buffer, bytesRead, messageLength - bytesRead);
                    if (read == 0)
                        throw new IOException("Connection closed");
                    bytesRead += read;
                }
                
                var message = Encoding.UTF8.GetString(buffer, 0, messageLength);
                var baseMessage = MessageSerializer.Deserialize(message);
                
                if (baseMessage != null)
                {
                    OnMessageReceived?.Invoke(clientId, baseMessage);
                    await ProcessMessageAsync(clientId, baseMessage, client);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Signaling] Client {clientId} error: {ex.Message}");
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            _streams.TryRemove(clientId, out _);
            if (_sendLocks.TryRemove(clientId, out var sendLock))
            {
                sendLock.Dispose();
            }
            
            if (_clientToNode.TryRemove(clientId, out var nodeId))
            {
                _nodes.TryRemove(nodeId, out _);
                Console.WriteLine($"[Signaling] Node unregistered: {nodeId}");
            }
            
            client.Dispose();
            Console.WriteLine($"[Signaling] Client disconnected: {clientId}");
        }
    }
    
    private async Task ProcessMessageAsync(string clientId, BaseMessage message, TcpClient client)
    {
        switch (message.Type)
        {
            case MessageType.Register:
                await HandleRegisterAsync(clientId, (RegisterMessage)message, client);
                break;
            
            case MessageType.ConnectRequest:
                await HandleConnectRequestAsync(clientId, (ConnectRequest)message);
                break;
            
            case MessageType.HolePunchReady:
                await HandleHolePunchReadyAsync(clientId, (HolePunchStart)message);
                break;
            
            case MessageType.RelayRequest:
                await HandleRelayRequestAsync(clientId, (RelayRequest)message);
                break;
        }
    }
    
    private async Task HandleRegisterAsync(string clientId, RegisterMessage message, TcpClient client)
    {
        IPEndPoint? publicEndPoint = null;
        if (!string.IsNullOrWhiteSpace(message.PublicEndPoint) &&
            IPEndPoint.TryParse(message.PublicEndPoint, out var registeredEndPoint))
        {
            publicEndPoint = registeredEndPoint;
        }

        IPEndPoint? publicEndPointV6 = null;
        if (!string.IsNullOrWhiteSpace(message.PublicEndPointV6) &&
            IPEndPoint.TryParse(message.PublicEndPointV6, out var registeredEndPointV6))
        {
            publicEndPointV6 = registeredEndPointV6;
        }

        var nodeInfo = new NodeInfo
        {
            NodeId = message.NodeId,
            ClientId = clientId,
            PublicEndPoint = publicEndPoint,
            PublicEndPointV6 = publicEndPointV6,
            LastSeen = DateTime.UtcNow,
            IsIntranet = true,
            NatType = message.NatType
        };
        
        _nodes[message.NodeId] = nodeInfo;
        _clientToNode[clientId] = message.NodeId;
        
        var ack = new RegisterAck
        {
            Success = true,
            Message = "Registered successfully"
        };
        
        await SendToClientAsync(clientId, ack);
        Console.WriteLine($"[Signaling] Node registered: {message.NodeId} (IPv4={publicEndPoint}, IPv6={publicEndPointV6}, NatType={message.NatType})");
    }
    
    private async Task HandleConnectRequestAsync(string clientId, ConnectRequest message)
    {
        if (!_nodes.TryGetValue(message.TargetNodeId, out var targetNode))
        {
            var error = new ErrorMessage
            {
                Code = 404,
                Message = $"Target node {message.TargetNodeId} not found"
            };
            await SendToClientAsync(clientId, error);
            return;
        }
        
        IPEndPoint? requestEndPoint = null;
        if (!string.IsNullOrWhiteSpace(message.ClientEndPoint) &&
            IPEndPoint.TryParse(message.ClientEndPoint, out var parsedRequestEndPoint))
        {
            requestEndPoint = parsedRequestEndPoint;
        }

        IPEndPoint? requestEndPointV6 = null;
        if (!string.IsNullOrWhiteSpace(message.ClientEndPointV6) &&
            IPEndPoint.TryParse(message.ClientEndPointV6, out var parsedRequestEndPointV6))
        {
            requestEndPointV6 = parsedRequestEndPointV6;
        }

        if ((targetNode.PublicEndPoint == null && targetNode.PublicEndPointV6 == null) ||
            (requestEndPoint == null && requestEndPointV6 == null))
        {
            var error = new ErrorMessage
            {
                Code = 426,
                Message = "UDP hole punch endpoint unavailable; relay required"
            };
            await SendToClientAsync(clientId, error);
            return;
        }
        
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        var conv = (uint)Random.Shared.Next(1, int.MaxValue);

        // 通知目标节点开始打洞
        var holePunchStart = new HolePunchStart
        {
            SessionId = sessionId,
            TargetEndPoint = requestEndPoint?.ToString() ?? string.Empty,
            TargetEndPointV6 = requestEndPointV6?.ToString(),
            IsInitiator = true,
            Conv = conv,
            TargetNatType = message.NatType
        };
        await SendToClientAsync(targetNode.ClientId, holePunchStart);
        
        // 通知请求方准备打洞
        var connectReady = new ConnectReady
        {
            SessionId = sessionId,
            IntranetEndPoint = targetNode.PublicEndPoint?.ToString() ?? string.Empty,
            IntranetEndPointV6 = targetNode.PublicEndPointV6?.ToString(),
            ExtranetEndPoint = requestEndPoint?.ToString() ?? string.Empty,
            ExtranetEndPointV6 = requestEndPointV6?.ToString(),
            RelayAvailable = true,
            Conv = conv,
            TargetNatType = targetNode.NatType
        };
        await SendToClientAsync(clientId, connectReady);
    }
    
    private async Task HandleHolePunchReadyAsync(string clientId, HolePunchStart message)
    {
        // 打洞就绪，可以开始P2P连接
        Console.WriteLine($"[Signaling] Hole punch ready for client {clientId}");
    }
    
    private async Task HandleRelayRequestAsync(string clientId, RelayRequest message)
    {
        if (!_nodes.TryGetValue(message.TargetNodeId, out var targetNode))
        {
            var error = new ErrorMessage
            {
                Code = 404,
                Message = $"Target node {message.TargetNodeId} not found"
            };
            await SendToClientAsync(clientId, error);
            return;
        }
        
        // 创建中转会话
        var sessionId = string.IsNullOrWhiteSpace(message.SessionId)
            ? Guid.NewGuid().ToString("N")[..8]
            : message.SessionId;
        var extranetRelayAccept = new RelayAccept
        {
            RelayPort = _relayPort,
            SessionId = sessionId,
            Role = "extranet"
        };

        var intranetRelayAccept = new RelayAccept
        {
            RelayPort = _relayPort,
            SessionId = sessionId,
            Role = "intranet"
        };
        
        await SendToClientAsync(clientId, extranetRelayAccept);
        await SendToClientAsync(targetNode.ClientId, intranetRelayAccept);
    }
    
    public async Task SendToClientAsync(string clientId, BaseMessage message)
    {
        if (!_streams.TryGetValue(clientId, out var stream))
            return;
        if (!_sendLocks.TryGetValue(clientId, out var sendLock))
            return;
        
        await sendLock.WaitAsync();
        try
        {
            var json = MessageSerializer.SerializeToString(message);
            var data = Encoding.UTF8.GetBytes(json);
            var lengthBytes = BitConverter.GetBytes(data.Length);
            
            await stream.WriteAsync(lengthBytes, 0, 4);
            await stream.WriteAsync(data, 0, data.Length);
            await stream.FlushAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Signaling] Send error to {clientId}: {ex.Message}");
        }
        finally
        {
            sendLock.Release();
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
        foreach (var sendLock in _sendLocks.Values)
        {
            sendLock.Dispose();
        }
        _sendLocks.Clear();
        _nodes.Clear();
        _clientToNode.Clear();
    }
}

/// <summary>
/// 中转服务
/// </summary>
public class RelayService : IDisposable
{
    private readonly TcpListener _listener;
    private readonly int _port;
    private readonly int _maxSessions;
    private readonly ConcurrentDictionary<string, RelaySession> _sessions = new();
    private CancellationTokenSource _cts;
    private bool _isRunning;
    
    public RelayService(int port, int maxSessions = 100)
    {
        _port = port;
        _maxSessions = maxSessions;
        _listener = new TcpListener(IPAddress.Any, port);
        _cts = new CancellationTokenSource();
    }
    
    public async Task StartAsync()
    {
        _listener.Start();
        _isRunning = true;
        Console.WriteLine($"[Relay] Listening on TCP port {_port}");
        
        while (_isRunning)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleRelayClientAsync(client));
            }
            catch (Exception) when (!_isRunning)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Relay] Accept error: {ex.Message}");
            }
        }
    }
    
    private async Task HandleRelayClientAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[65536];
            
            // 读取会话ID
            var lengthBuffer = new byte[4];
            int bytesRead = 0;
            while (bytesRead < 4)
            {
                int read = await stream.ReadAsync(lengthBuffer, bytesRead, 4 - bytesRead);
                if (read == 0)
                    return;
                bytesRead += read;
            }
            
            int messageLength = BitConverter.ToInt32(lengthBuffer, 0);
            bytesRead = 0;
            while (bytesRead < messageLength)
            {
                int read = await stream.ReadAsync(buffer, bytesRead, messageLength - bytesRead);
                if (read == 0)
                    return;
                bytesRead += read;
            }
            
            var handshake = Encoding.UTF8.GetString(buffer, 0, messageLength);
            var parts = handshake.Split('|', 2);
            var sessionId = parts[0];
            var role = parts.Length > 1 ? parts[1] : string.Empty;
            
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                if (_sessions.Count >= _maxSessions)
                {
                    Console.WriteLine($"[Relay] Rejecting session {sessionId}: max sessions reached");
                    client.Dispose();
                    return;
                }

                session = new RelaySession
                {
                    SessionId = sessionId,
                    CreatedAt = DateTime.UtcNow,
                    IsActive = true
                };
                _sessions[sessionId] = session;
            }
            
            if (string.Equals(role, "intranet", StringComparison.OrdinalIgnoreCase))
            {
                session.IntranetClient?.Dispose();
                session.IntranetClient = client;
                session.IntranetClientId = sessionId;
                Console.WriteLine($"[Relay] Intranet client connected to session {sessionId}");
            }
            else if (string.Equals(role, "extranet", StringComparison.OrdinalIgnoreCase))
            {
                session.ExtranetClient?.Dispose();
                session.ExtranetClient = client;
                session.ExtranetClientId = sessionId;
                Console.WriteLine($"[Relay] Extranet client connected to session {sessionId}");
            }
            else if (session.IntranetClient == null)
            {
                session.IntranetClient = client;
                session.IntranetClientId = sessionId;
                Console.WriteLine($"[Relay] Client connected as intranet fallback to session {sessionId}");
            }
            else if (session.ExtranetClient == null)
            {
                session.ExtranetClient = client;
                session.ExtranetClientId = sessionId;
                Console.WriteLine($"[Relay] Client connected as extranet fallback to session {sessionId}");
            }

            if (session.IntranetClient != null && session.ExtranetClient != null)
            {
                await StartRelayAsync(session);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Relay] Client error: {ex.Message}");
        }
    }
    
    private async Task StartRelayAsync(RelaySession session)
    {
        if (session.IntranetClient == null || session.ExtranetClient == null)
            return;
        
        var intranetStream = session.IntranetClient.GetStream();
        var extranetStream = session.ExtranetClient.GetStream();
        
        var task1 = RelayStreamAsync(intranetStream, extranetStream, session);
        var task2 = RelayStreamAsync(extranetStream, intranetStream, session);
        
        await Task.WhenAny(task1, task2);
        
        // 清理
        session.IsActive = false;
        session.IntranetClient?.Dispose();
        session.ExtranetClient?.Dispose();
        _sessions.TryRemove(session.SessionId, out _);
        
        Console.WriteLine($"[Relay] Session {session.SessionId} ended");
    }
    
    private async Task RelayStreamAsync(NetworkStream source, NetworkStream destination, RelaySession session)
    {
        var buffer = new byte[65536];
        
        try
        {
            while (session.IsActive)
            {
                int bytesRead = await source.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                    break;
                
                await destination.WriteAsync(buffer, 0, bytesRead);
                await destination.FlushAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Relay] Stream error: {ex.Message}");
        }
    }
    
    public void Dispose()
    {
        _isRunning = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _listener.Stop();
        
        foreach (var session in _sessions.Values)
        {
            session.IntranetClient?.Dispose();
            session.ExtranetClient?.Dispose();
        }
        
        _sessions.Clear();
    }
}
