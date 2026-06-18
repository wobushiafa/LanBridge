using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Concurrent;
using LanBridge.Common.Protocol;

namespace LanBridge.Common.Network;

public enum PeerConnectionRole
{
    Intranet,
    Extranet
}

public sealed class PeerConnectionOptions
{
    public PeerConnectionRole Role { get; init; }
    public string NodeId { get; init; } = string.Empty;
    public string SignalingServerHost { get; init; } = "127.0.0.1";
    public int SignalingServerPort { get; init; } = 9000;
    public string StunServerHost { get; init; } = "127.0.0.1";
    public int StunServerPort { get; init; } = 9001;
    public int StunAlternateServerPort { get; init; } = 9003;
    public string Token { get; init; } = "default-token";
    public int UdpPort { get; init; }
    public string TargetNodeId { get; init; } = string.Empty;
    public int HolePunchTimeoutMs { get; init; } = 10000;
    public bool EnableRelayFallback { get; init; } = true;
    public bool Verbose { get; init; }
    public bool EnableKcpCongestionControl { get; init; } = false;
}

public sealed class ConnectionNegotiator : IDisposable
{
    private readonly PeerConnectionOptions _options;
    private readonly PeerNatDiagnostics _natDiagnostics;
    private readonly SignalingMessageRouter _signalingMessageRouter;
    private readonly ConcurrentDictionary<string, PeerTransportSession> _sessions = new();
    private readonly ConcurrentDictionary<string, PendingPunch> _pendingPunches = new();
    private readonly SemaphoreSlim _connectionRequestLock = new(1, 1);
    private readonly SignalingConnectionLoop _signalingConnectionLoop;
    private UdpHolePuncher? _holePuncher;
    private LanDiscoveryService? _lanDiscovery;
    private IPEndPoint? _publicEndPoint;
    private IPEndPoint? _publicEndPointV6;
    private NatDetectionResult? _natDetection;
    private CancellationTokenSource? _relayProbeCts;
    private bool _isHolePunching;
    private string _activeSessionId = "default";
    private DateTime _lastConnectionRequestUtc = DateTime.MinValue;
    private readonly CancellationTokenSource _cts = new();
    private bool _isNatKeepAliveRunning;

    public event Action<string>? OnStatusChanged;
    public event Action<byte[], int>? OnDataReceived;
    public event Action<string, byte[], int>? OnSessionDataReceived;
    public event Action<PeerTransportMode>? OnModeChanged;
    public event Action? OnSignalingDisconnected;

    public IPEndPoint? PublicEndPoint => _publicEndPoint;
    public PeerTransportMode Mode => GetSession(_activeSessionId).Mode;
    public bool IsConnected => GetSession(_activeSessionId).IsConnected;
    public bool IsSignalingConnected => _signalingConnectionLoop.IsConnected;

    public ConnectionNegotiator(PeerConnectionOptions options)
    {
        _options = options;
        _natDiagnostics = new PeerNatDiagnostics(options, status => OnStatusChanged?.Invoke(status));
        _signalingMessageRouter = new SignalingMessageRouter(
            status => OnStatusChanged?.Invoke(status),
            HandleConnectReadyAsync,
            HandleHolePunchStartAsync,
            HandleRelayAcceptAsync,
            HandleErrorAsync);
        _signalingConnectionLoop = new SignalingConnectionLoop(
            _options.SignalingServerHost,
            _options.SignalingServerPort,
            status => OnStatusChanged?.Invoke(status),
            () => OnSignalingDisconnected?.Invoke(),
            message => _signalingMessageRouter.DispatchAsync(message),
            HandleSignalingConnectedAsync);
    }

    private sealed record PendingPunch(string SessionId, uint Conv);

    private static string GetAddressKey(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }
        return address.ToString();
    }

    public void RaiseStatusChanged(string status)
    {
        OnStatusChanged?.Invoke(status);
    }

    public async Task HandleLanDiscoveryRequestAsync(IPEndPoint clientEndPoint, uint conv)
    {
        var sessionId = _activeSessionId;
        
        // Register the client in pending punches so it gets picked up by MarkPunched
        _pendingPunches[GetAddressKey(clientEndPoint.Address)] = new PendingPunch(sessionId, conv);
        
        if (_holePuncher != null)
        {
            OnStatusChanged?.Invoke($"[LAN Discovery] Received local query from {clientEndPoint}. Replying advertisement & establishing direct KCP session...");
            
            // Reply with unicast advertisement containing target server port
            var serverPort = _holePuncher.LocalEndPoint?.Port ?? 0;
            var advertiseMessage = $"LB_ADVERTISE:{_options.NodeId}:{serverPort}:{conv}";
            var data = Encoding.UTF8.GetBytes(advertiseMessage);
            await _holePuncher.SendAsync(data, data.Length, clientEndPoint);
            
            // Directly trigger punch completion!
            _holePuncher.TriggerHolePunched(clientEndPoint, conv);
        }
    }

    public async Task StartAsync()
    {
        _holePuncher = new UdpHolePuncher(_options.UdpPort, _options.NodeId);
        ConfigureHolePuncherEvents();

        // Start LAN Discovery Service to scan and respond to local subnets
        _lanDiscovery = new LanDiscoveryService(_options.NodeId, this, _options.Verbose);
        _lanDiscovery.Start();

        if (_options.Role == PeerConnectionRole.Extranet)
        {
            StartLanDiscoveryBroadcast();
        }

        await DetectNatAsync();

        // Start signaling connection manager loop in background
        _ = Task.Run(() => _signalingConnectionLoop.RunAsync(_cts.Token), _cts.Token);
    }

    private void StartNatKeepAliveLoop()
    {
        if (_options.Role != PeerConnectionRole.Intranet)
        {
            return;
        }

        if (_isNatKeepAliveRunning)
        {
            return;
        }
        _isNatKeepAliveRunning = true;

        _ = _natDiagnostics.RunKeepAliveLoopAsync(
            _holePuncher!,
            _cts.Token,
            shouldProbe: () => IsSignalingConnected && !IsConnected && !_isHolePunching,
            currentPublicEndPointProvider: () => _publicEndPoint,
            onMappingChangedAsync: async endpoint =>
            {
                _publicEndPoint = endpoint;
                await RegisterNodeAsync();
            }).ContinueWith(_ =>
        {
            _isNatKeepAliveRunning = false;
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    public async Task<bool> EnsureConnectedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (IsConnected)
        {
            return true;
        }

        if (_options.Role == PeerConnectionRole.Extranet)
        {
            await RequestConnectionAsync(force: false);
        }

        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsConnected)
            {
                return true;
            }

            await Task.Delay(100, cancellationToken);
        }

        return IsConnected;
    }

    public async Task SendAsync(byte[] data, int offset, int length)
    {
        await SendAsync(_activeSessionId, data, offset, length);
    }

    public async Task SendAsync(string sessionId, byte[] data, int offset, int length)
    {
        await GetSession(sessionId).SendAsync(data, offset, length);
    }

    public async Task SendUnreliableAsync(byte[] data, int offset, int length)
    {
        await SendUnreliableAsync(_activeSessionId, data, offset, length);
    }

    public async Task SendUnreliableAsync(string sessionId, byte[] data, int offset, int length)
    {
        var session = GetSession(sessionId);
        if (session.Mode == PeerTransportMode.P2pDirect && _holePuncher?.RemoteEndPoint != null)
        {
            try
            {
                await _holePuncher.Client.SendAsync(new ReadOnlyMemory<byte>(data, offset, length), _holePuncher.RemoteEndPoint);
                return;
            }
            catch (Exception ex)
            {
                if (_options.Verbose)
                {
                    OnStatusChanged?.Invoke($"Unreliable direct send failed: {ex.Message}");
                }
            }
        }

        await session.SendAsync(data, offset, length);
    }

    private PeerTransportSession GetSession(string sessionId)
    {
        return _sessions.GetOrAdd(sessionId, id =>
        {
            var transport = new PeerTransportSession(_options.Verbose);
            transport.OnDataReceived += (data, length) =>
            {
                OnDataReceived?.Invoke(data, length);
                OnSessionDataReceived?.Invoke(id, data, length);
            };
            transport.OnDisconnected += () =>
            {
                if (id == _activeSessionId)
                {
                    OnModeChanged?.Invoke(PeerTransportMode.None);
                }
                OnStatusChanged?.Invoke($"Transport disconnected: session {id}");
            };
            transport.OnStatusChanged += status => OnStatusChanged?.Invoke(status);
            transport.OnP2pUnhealthy += reason => _ = HandleP2pUnhealthyAsync(id, reason);
            return transport;
        });
    }

    public async Task RequestConnectionAsync(bool force = false)
    {
        if (IsConnected)
        {
            return;
        }

        if (_options.Role != PeerConnectionRole.Extranet)
        {
            return;
        }

        if (_signalingConnectionLoop.Client?.IsConnected != true)
        {
            return;
        }

        await _connectionRequestLock.WaitAsync();
        try
        {
            if (!force && DateTime.UtcNow - _lastConnectionRequestUtc <= TimeSpan.FromSeconds(2))
            {
                return;
            }

            var request = new ConnectRequest
            {
                TargetNodeId = _options.TargetNodeId,
                ClientEndPoint = _publicEndPoint?.ToString(),
                ClientEndPointV6 = _publicEndPointV6?.ToString(),
                NatType = _natDetection?.NatType ?? StunNatType.Unknown
            };

            await _signalingConnectionLoop.Client.SendAsync(MessageSerializer.SerializeToString(request));
            _lastConnectionRequestUtc = DateTime.UtcNow;
            OnStatusChanged?.Invoke($"Connection requested to {_options.TargetNodeId}");
        }
        finally
        {
            _connectionRequestLock.Release();
        }
    }

    private async Task DetectNatAsync()
    {
        var snapshot = await _natDiagnostics.DetectAsync(_holePuncher!);
        _natDetection = snapshot.Detection;
        _publicEndPoint = snapshot.PublicEndPoint;
        _publicEndPointV6 = snapshot.PublicEndPointV6;
    }

    private async Task RegisterNodeAsync()
    {
        var register = new RegisterMessage
        {
            NodeId = _options.NodeId,
            Token = _options.Token,
            PublicEndPoint = _publicEndPoint?.ToString(),
            PublicEndPointV6 = _publicEndPointV6?.ToString(),
            NatType = _natDetection?.NatType ?? StunNatType.Unknown
        };

        await _signalingConnectionLoop.Client!.SendAsync(MessageSerializer.SerializeToString(register));
    }

    private async Task HandleConnectReadyAsync(ConnectReady message)
    {
        if (IsConnected)
        {
            return;
        }

        var sessionId = string.IsNullOrWhiteSpace(message.SessionId) ? _activeSessionId : message.SessionId;
        _activeSessionId = sessionId;

        await BeginHolePunchFromSignalAsync(
            sessionId,
            _options.Role == PeerConnectionRole.Intranet ? message.ExtranetEndPoint : message.IntranetEndPoint,
            _options.Role == PeerConnectionRole.Intranet ? message.ExtranetEndPointV6 : message.IntranetEndPointV6,
            message.Conv,
            message.TargetNatType,
            $"Connection ready. Starting hole punch to {{0}}");
    }

    private async Task HandleHolePunchStartAsync(HolePunchStart message)
    {
        if (IsConnected)
        {
            return;
        }

        var sessionId = string.IsNullOrWhiteSpace(message.SessionId) ? Guid.NewGuid().ToString("N")[..8] : message.SessionId;

        await BeginHolePunchFromSignalAsync(
            sessionId,
            message.TargetEndPoint,
            message.TargetEndPointV6,
            message.Conv,
            message.TargetNatType,
            $"Hole punch request to {{0}}");
    }

    private async Task StartHolePunchAsync(string sessionId, uint conv, IPEndPoint? targetEndPoint, IPEndPoint? targetEndPointV6, bool allowRelayFallback, StunNatType targetNatType)
    {
        if (GetSession(sessionId).Mode == PeerTransportMode.P2pDirect)
        {
            return;
        }

        _isHolePunching = true;
        
        if (targetEndPoint != null)
        {
            _pendingPunches[GetAddressKey(targetEndPoint.Address)] = new PendingPunch(sessionId, conv);
        }
        if (targetEndPointV6 != null)
        {
            _pendingPunches[GetAddressKey(targetEndPointV6.Address)] = new PendingPunch(sessionId, conv);
        }
        
        OnModeChanged?.Invoke(PeerTransportMode.None);
        OnStatusChanged?.Invoke("State: HolePunching");

        if (allowRelayFallback && _options.EnableRelayFallback)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(_options.HolePunchTimeoutMs + 1000);
                if (_isHolePunching && !IsConnected)
                {
                    OnStatusChanged?.Invoke("Hole punch timeout. Falling back to relay...");
                    OnStatusChanged?.Invoke($"P2P failure reason: {ExplainP2pFailure()}");
                    await RequestRelayAsync();
                }
            });
        }

        bool predictPorts = _natDetection != null && 
                            _natDetection.NatType != StunNatType.Symmetric && 
                            _natDetection.NatType != StunNatType.Blocked && 
                            targetNatType == StunNatType.Symmetric;

        if (predictPorts)
        {
            OnStatusChanged?.Invoke("Target is Symmetric NAT. Enabling port prediction for hole punching.");
        }

        if (targetEndPoint != null)
        {
            await _holePuncher!.StartPunchingAsync(targetEndPoint, targetEndPointV6, 200, _options.HolePunchTimeoutMs, predictPorts);
        }
        else if (targetEndPointV6 != null)
        {
            await _holePuncher!.StartPunchingAsync(targetEndPointV6, null, 200, _options.HolePunchTimeoutMs, predictPorts);
        }
    }

    private void ConfigureHolePuncherEvents()
    {
        _holePuncher!.OnHolePunched += endpoint =>
        {
            OnStatusChanged?.Invoke($"Hole punched to {endpoint}");
            _isHolePunching = false;

            if (!_pendingPunches.TryRemove(GetAddressKey(endpoint.Address), out var pending))
            {
                OnStatusChanged?.Invoke($"Hole punched endpoint has no pending session: {endpoint}");
                _holePuncher.RemovePunchedEndpoint(endpoint);
                return;
            }

            var conv = pending.Conv != 0 ? pending.Conv : P2PConv.FromEndpoints(_publicEndPoint!, endpoint);
            OnStatusChanged?.Invoke($"P2P conv: {conv}");
            _relayProbeCts?.Cancel();
            var session = new KcpSession(conv, _holePuncher.Client, endpoint, enableCongestionControl: _options.EnableKcpCongestionControl, ownReceiveLoop: false);
            _holePuncher.RegisterKcpSession(session);
            GetSession(pending.SessionId).UseP2p(session);
            if (pending.SessionId == _activeSessionId)
            {
                OnModeChanged?.Invoke(PeerTransportMode.P2pDirect);
            }
        };

        _holePuncher.OnLanAdvertised += (endpoint, conv) =>
        {
            if (IsConnected) return;

            OnStatusChanged?.Invoke($"[LAN Discovery] Discovered local peer at {endpoint}, conv={conv}. Direct-connecting...");
            _isHolePunching = false;

            var sessionId = _activeSessionId;
            _pendingPunches[GetAddressKey(endpoint.Address)] = new PendingPunch(sessionId, conv);

            _holePuncher.TriggerHolePunched(endpoint, conv);
        };

        _holePuncher.OnError += error =>
        {
            OnStatusChanged?.Invoke($"Hole punch error: {error}");
            if (error.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            {
                OnStatusChanged?.Invoke($"P2P failure reason: {ExplainP2pFailure()}");
            }
        };

        _holePuncher.OnUnreliableDataReceived += (buffer, length, endpoint) =>
        {
            if (_holePuncher.RemoteEndPoint != null)
            {
                var addr1 = endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address;
                var addr2 = _holePuncher.RemoteEndPoint.Address.IsIPv4MappedToIPv6 ? _holePuncher.RemoteEndPoint.Address.MapToIPv4() : _holePuncher.RemoteEndPoint.Address;
                if (addr1.Equals(addr2))
                {
                    OnDataReceived?.Invoke(buffer, length);
                    OnSessionDataReceived?.Invoke(_activeSessionId, buffer, length);
                }
            }
        };
    }

    private string ExplainP2pFailure()
    {
        return P2pFailureExplainer.Describe(_natDetection);
    }

    private async Task HandleRelayAcceptAsync(RelayAccept message)
    {
        OnStatusChanged?.Invoke($"Relay accepted. Session: {message.SessionId}");
        var sessionId = string.IsNullOrWhiteSpace(message.SessionId) ? _activeSessionId : message.SessionId;
        _activeSessionId = sessionId;

        var relayClient = new RelayClient();
        await relayClient.ConnectAsync(_options.SignalingServerHost, message.RelayPort);

        var sessionData = Encoding.UTF8.GetBytes($"{message.SessionId}|{message.Role}");
        await relayClient.SendAsync(sessionData, 0, sessionData.Length);

        _isHolePunching = false;
        GetSession(sessionId).UseRelay(relayClient);
        OnModeChanged?.Invoke(PeerTransportMode.Relay);
        StartRelayProbeLoop();
    }

    private async Task HandleP2pUnhealthyAsync(string sessionId, string reason)
    {
        OnStatusChanged?.Invoke($"P2P unhealthy on session {sessionId}: {reason}");
        await RequestRelayIfAllowedAsync();
    }

    private async Task HandleErrorAsync(ErrorMessage error)
    {
        OnStatusChanged?.Invoke($"Error: {error.Message}");
        if (error.Code == 404)
        {
            return;
        }

        await RequestRelayIfAllowedAsync();
    }

    private async Task RequestRelayIfAllowedAsync()
    {
        if (_options.Role == PeerConnectionRole.Extranet && _options.EnableRelayFallback && Mode != PeerTransportMode.Relay)
        {
            OnStatusChanged?.Invoke("Falling back to relay mode...");
            await RequestRelayAsync();
        }
    }

    private async Task RequestRelayAsync()
    {
        if (_signalingConnectionLoop.Client?.IsConnected != true || string.IsNullOrWhiteSpace(_options.TargetNodeId))
        {
            return;
        }

        var request = new RelayRequest
        {
            TargetNodeId = _options.TargetNodeId,
            SessionId = _activeSessionId
        };

        await _signalingConnectionLoop.Client.SendAsync(MessageSerializer.SerializeToString(request));
    }

    private void StartRelayProbeLoop()
    {
        if (_options.Role != PeerConnectionRole.Extranet || !_options.EnableRelayFallback)
        {
            return;
        }

        _relayProbeCts?.Cancel();
        _relayProbeCts?.Dispose();
        _relayProbeCts = new CancellationTokenSource();
        var token = _relayProbeCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && Mode == PeerTransportMode.Relay)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                    if (token.IsCancellationRequested || Mode != PeerTransportMode.Relay)
                    {
                        break;
                    }

                    OnStatusChanged?.Invoke("Relay is active. Probing whether P2P can be restored...");
                    await RequestConnectionAsync(force: true);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    OnStatusChanged?.Invoke($"P2P restore probe failed: {ex.Message}");
                }
            }
        }, token);
    }

    private void StartLanDiscoveryBroadcast()
    {
        var clientPort = _holePuncher!.LocalEndPoint?.Port ?? 0;
        var tempConv = (uint)Random.Shared.Next(100000, 99999999);
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                if (IsConnected)
                {
                    break;
                }

                await _lanDiscovery!.BroadcastQueryAsync(_options.TargetNodeId, clientPort, tempConv);
                await Task.Delay(100);
            }
        });
    }

    private async Task BeginHolePunchFromSignalAsync(
        string sessionId,
        string? target,
        string? targetV6,
        uint conv,
        StunNatType targetNatType,
        string statusFormat)
    {
        if (string.IsNullOrWhiteSpace(target) && string.IsNullOrWhiteSpace(targetV6))
        {
            OnStatusChanged?.Invoke("No remote UDP endpoint available. Falling back to relay...");
            await RequestRelayIfAllowedAsync();
            return;
        }

        var targetEp = TryParseEndPoint(target);
        var targetEpV6 = TryParseEndPoint(targetV6);
        var displayTarget = targetEpV6 != null ? $"{targetEp} / {targetEpV6}" : targetEp?.ToString() ?? string.Empty;
        OnStatusChanged?.Invoke(string.Format(statusFormat, displayTarget));

        await StartHolePunchAsync(
            sessionId,
            conv,
            targetEp,
            targetEpV6,
            allowRelayFallback: _options.Role == PeerConnectionRole.Extranet,
            targetNatType);
    }

    private static IPEndPoint? TryParseEndPoint(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && IPEndPoint.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
        _connectionRequestLock.Dispose();
        _relayProbeCts?.Cancel();
        _relayProbeCts?.Dispose();
        _signalingConnectionLoop.Dispose();
        _holePuncher?.Dispose();
        _lanDiscovery?.Dispose();
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();
    }

    private async Task HandleSignalingConnectedAsync(SignalingClient client)
    {
        if (_options.Role == PeerConnectionRole.Intranet)
        {
            await RegisterNodeAsync();
            OnStatusChanged?.Invoke("Ready");
            StartNatKeepAliveLoop();
        }
        else
        {
            await RequestConnectionAsync(force: true);
        }
    }
}
