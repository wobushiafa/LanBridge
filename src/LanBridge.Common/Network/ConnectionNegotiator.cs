using System.Net;
using System.Text;
using LanBridge.Common.Diagnostics;
using LanBridge.Common.Protocol;

namespace LanBridge.Common.Network;

public enum PeerConnectionRole
{
    Intranet,
    Extranet
}

public sealed record PeerConnectionOptions
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
    public string SignalingTransport { get; init; } = "tcp";
    public int SignalingWsPort { get; init; } = 9010;
    public long RateLimitBytesPerSec { get; init; }
    public FramePriority Priority { get; init; } = FramePriority.Normal;
    public bool EnablePortMapping { get; init; } = false;
    public int ExternalPort { get; init; } = 0;
}

public sealed class ConnectionNegotiator : IDisposable, ISignalingHandler, ILanDiscoveryHost
{
    private readonly PeerConnectionOptions _options;
    private readonly PeerSessionManager _sessions;
    private readonly PeerPunchCoordinator _punch;
    private readonly SemaphoreSlim _connectionRequestLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    // Shared infrastructure (injected for multi-tunnel, or self-owned for standalone)
    private readonly SharedUdpStack? _injectedUdpStack;
    private readonly SharedSignalingStack? _injectedSignalingStack;
    private readonly bool _ownsSharedStacks;

    // Self-owned instances for standalone mode
    private readonly SignalingMessageRouter? _standaloneRouter;
    private readonly SignalingConnectionLoop _signalingConnectionLoop;
    private LanDiscoveryService? _lanDiscovery;

    private DateTime _lastConnectionRequestUtc = DateTime.MinValue;

    public event Action<string>? OnStatusChanged;
    public event Action<byte[], int>? OnDataReceived;
    public event Action<string, byte[], int>? OnSessionDataReceived;
    public event Action<PeerTransportMode>? OnModeChanged;
    public event Action? OnSignalingDisconnected;

    public IPEndPoint? PublicEndPoint => _punch.PublicEndPoint;
    public PeerTransportMode Mode => _sessions.Mode;
    public bool IsConnected => _sessions.IsConnected;
    public bool IsSignalingConnected => _signalingConnectionLoop.IsConnected;
    public string TargetNodeId => _options.TargetNodeId;

    /// <summary>
    /// Standalone constructor — creates and owns all infrastructure internally.
    /// Used by IntranetPeer and single-target ExtranetPeer.
    /// </summary>
    public ConnectionNegotiator(PeerConnectionOptions options)
    {
        _options = options;
        _ownsSharedStacks = true;

        _sessions = new PeerSessionManager(options);
        WireSessionManagerEvents();

        _standaloneRouter = new SignalingMessageRouter(
            status => OnStatusChanged?.Invoke(status),
            HandleConnectReadyAsync,
            HandleHolePunchStartAsync,
            HandleRelayAcceptAsync,
            ProcessErrorAsync);

        _signalingConnectionLoop = new SignalingConnectionLoop(
            _options.SignalingServerHost,
            _options.SignalingServerPort,
            status => OnStatusChanged?.Invoke(status),
            () => OnSignalingDisconnected?.Invoke(),
            message => _standaloneRouter.DispatchAsync(message),
            HandleSignalingConnectedAsync,
            _options.SignalingTransport,
            _options.SignalingWsPort);

        _punch = CreatePunchCoordinator();
        WirePunchCoordinatorEvents();
    }

    /// <summary>
    /// Shared-stack constructor — injects shared UDP and signaling infrastructure.
    /// Used by multi-tunnel ExtranetPeer (via TunnelRouter).
    /// </summary>
    public ConnectionNegotiator(
        PeerConnectionOptions options,
        SharedUdpStack udpStack,
        SharedSignalingStack signalingStack)
    {
        _options = options;
        _injectedUdpStack = udpStack;
        _injectedSignalingStack = signalingStack;
        _ownsSharedStacks = false;

        _sessions = new PeerSessionManager(options);
        WireSessionManagerEvents();

        // Use the shared signaling stack's connection loop and dispatcher
        _signalingConnectionLoop = signalingStack.ConnectionLoop;

        // Register ourselves as a handler for our target node and sessions
        signalingStack.Dispatcher.RegisterNode(_options.TargetNodeId, this);

        _punch = CreatePunchCoordinator();
        WirePunchCoordinatorEvents();
    }

    private PeerPunchCoordinator CreatePunchCoordinator()
    {
        return new PeerPunchCoordinator(
            _options,
            _sessions,
            attachP2p: (id, session) => _sessions.GetSession(id).UseP2p(session),
            isConnected: () => IsConnected,
            activeSessionIdProvider: () => _sessions.ActiveSessionId,
            isSignalingConnected: () => IsSignalingConnected,
            modeProvider: () => Mode,
            requestRelayAsync: () => RequestRelayAsync(),
            requestConnectionAsync: force => RequestConnectionAsync(force),
            registerNodeAsync: () => RegisterNodeAsync());
    }

    private void WireSessionManagerEvents()
    {
        _sessions.OnDataReceived += (data, length) => OnDataReceived?.Invoke(data, length);
        _sessions.OnSessionDataReceived += (id, data, length) => OnSessionDataReceived?.Invoke(id, data, length);
        _sessions.OnModeChanged += mode => OnModeChanged?.Invoke(mode);
        _sessions.OnStatusChanged += status => OnStatusChanged?.Invoke(status);
        _sessions.OnP2pUnhealthy += (id, reason) => _ = HandleP2pUnhealthyAsync(id, reason);
    }

    private void WirePunchCoordinatorEvents()
    {
        _punch.OnStatusChanged += status => OnStatusChanged?.Invoke(status);
        _punch.OnModeChanged += mode => OnModeChanged?.Invoke(mode);
        _punch.OnUnreliableDataReceived += (data, length, sessionId) =>
        {
            OnDataReceived?.Invoke(data, length);
            OnSessionDataReceived?.Invoke(sessionId, data, length);
        };
    }

    public void RaiseStatusChanged(string status)
    {
        OnStatusChanged?.Invoke(status);
    }

    public Task HandleLanDiscoveryRequestAsync(IPEndPoint clientEndPoint, uint conv)
        => _punch.HandleLanDiscoveryRequestAsync(clientEndPoint, conv);

    public async Task StartAsync()
    {
        // Build/acquire hole puncher, wire its events, and run NAT detection.
        await _punch.StartAsync(_injectedUdpStack);

        if (_injectedUdpStack != null)
        {
            // Shared-stack mode: LAN discovery is managed by the shared stack.
            if (_options.Role == PeerConnectionRole.Intranet || _options.Role == PeerConnectionRole.Extranet)
            {
                _injectedUdpStack.StartLanDiscovery(this);
            }

            if (_options.Role == PeerConnectionRole.Extranet)
            {
                StartLanDiscoveryBroadcast();
            }

            // The shared signaling loop is already running, so just trigger our connection request
            if (_options.Role == PeerConnectionRole.Extranet)
            {
                await RequestConnectionAsync(force: true);
            }
            else
            {
                await RegisterNodeAsync();
                OnStatusChanged?.Invoke("Ready");
                _punch.StartNatKeepAliveLoop(_cts.Token);
            }
        }
        else
        {
            // Standalone mode: LanDiscoveryService needs a ConnectionNegotiator reference
            // (circular dependency if the coordinator built it), so it stays here.
            _lanDiscovery = new LanDiscoveryService(_options.NodeId, this, _options.Verbose);
            _lanDiscovery.Start();

            if (_options.Role == PeerConnectionRole.Extranet)
            {
                StartLanDiscoveryBroadcast();
            }

            // Start signaling connection manager loop in background
            _ = Task.Run(() => _signalingConnectionLoop.RunAsync(_cts.Token), _cts.Token);
        }
    }

    public Task<bool> EnsureConnectedAsync(TimeSpan timeout, CancellationToken cancellationToken)
        => _punch.EnsureConnectedAsync(timeout, cancellationToken);

    public Task SendAsync(byte[] data, int offset, int length)
    {
        return _sessions.SendAsync(data, offset, length);
    }

    public Task SendAsync(string sessionId, byte[] data, int offset, int length)
    {
        return _sessions.SendAsync(sessionId, data, offset, length);
    }

    public Task SendHighPriorityAsync(string sessionId, byte[] data, int offset, int length)
    {
        return _sessions.SendHighPriorityAsync(sessionId, data, offset, length);
    }

    public Task SendHighPriorityAsync(byte[] data, int offset, int length)
    {
        return _sessions.SendHighPriorityAsync(data, offset, length);
    }

    public Task SendUnreliableAsync(byte[] data, int offset, int length)
        => _punch.SendUnreliableAsync(data, offset, length);

    public Task SendUnreliableAsync(string sessionId, byte[] data, int offset, int length)
        => _punch.SendUnreliableAsync(sessionId, data, offset, length);

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

        var client = _signalingConnectionLoop.Transport;
        if (client?.IsConnected != true)
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
                ClientEndPoint = _punch.PublicEndPoint?.ToString(),
                ClientEndPointV6 = _punch.PublicEndPointV6?.ToString(),
                NatType = _punch.NatDetection?.NatType ?? StunNatType.Unknown
            };

            await client.SendAsync(MessageSerializer.SerializeToString(request));
            _lastConnectionRequestUtc = DateTime.UtcNow;
            OnStatusChanged?.Invoke($"Connection requested to {_options.TargetNodeId}");
        }
        finally
        {
            _connectionRequestLock.Release();
        }
    }

    private async Task RegisterNodeAsync()
    {
        var client = _signalingConnectionLoop.Transport;
        if (client == null)
        {
            return;
        }

        var register = new RegisterMessage
        {
            NodeId = _options.NodeId,
            Token = _options.Token,
            PublicEndPoint = _punch.PublicEndPoint?.ToString(),
            PublicEndPointV6 = _punch.PublicEndPointV6?.ToString(),
            NatType = _punch.NatDetection?.NatType ?? StunNatType.Unknown
        };

        await client.SendAsync(MessageSerializer.SerializeToString(register));
    }

    // ─── ISignalingHandler implementation ───

    public Task HandleRegisterAckAsync(RegisterAck ack)
    {
        // Registration acks are logged by the dispatcher already
        return Task.CompletedTask;
    }

    public async Task HandleConnectReadyAsync(ConnectReady message)
    {
        if (IsConnected)
        {
            return;
        }

        var sessionId = string.IsNullOrWhiteSpace(message.SessionId) ? _sessions.ActiveSessionId : message.SessionId;
        _sessions.SetActiveSession(sessionId);

        // Register this session with the dispatcher so future messages for this session route to us
        if (_injectedSignalingStack != null)
        {
            _injectedSignalingStack.Dispatcher.RegisterSession(sessionId, this);
        }

        await _punch.BeginHolePunchFromSignalAsync(
            sessionId,
            _options.Role == PeerConnectionRole.Intranet ? message.ExtranetEndPoint : message.IntranetEndPoint,
            _options.Role == PeerConnectionRole.Intranet ? message.ExtranetEndPointV6 : message.IntranetEndPointV6,
            message.Conv,
            message.TargetNatType,
            $"Connection ready. Starting hole punch to {0}");
    }

    public async Task HandleHolePunchStartAsync(HolePunchStart message)
    {
        if (IsConnected)
        {
            return;
        }

        var sessionId = string.IsNullOrWhiteSpace(message.SessionId) ? Guid.NewGuid().ToString("N")[..8] : message.SessionId;

        // Register this session with the dispatcher
        if (_injectedSignalingStack != null)
        {
            _injectedSignalingStack.Dispatcher.RegisterSession(sessionId, this);
        }

        await _punch.BeginHolePunchFromSignalAsync(
            sessionId,
            message.TargetEndPoint,
            message.TargetEndPointV6,
            message.Conv,
            message.TargetNatType,
            $"Hole punch request to {0}");
    }

    public async Task HandleRelayAcceptAsync(RelayAccept message)
    {
        OnStatusChanged?.Invoke($"Relay accepted. Session: {message.SessionId}");
        var sessionId = string.IsNullOrWhiteSpace(message.SessionId) ? _sessions.ActiveSessionId : message.SessionId;
        _sessions.SetActiveSession(sessionId);

        // Register this session with the dispatcher
        if (_injectedSignalingStack != null)
        {
            _injectedSignalingStack.Dispatcher.RegisterSession(sessionId, this);
        }

        var relayClient = new RelayClient();
        await relayClient.ConnectAsync(_options.SignalingServerHost, message.RelayPort);

        var sessionData = Encoding.UTF8.GetBytes($"{message.SessionId}|{message.Role}");
        await relayClient.SendAsync(sessionData, 0, sessionData.Length);

        _punch.StopHolePunching();
        _sessions.GetSession(sessionId).UseRelay(relayClient);
        OnModeChanged?.Invoke(PeerTransportMode.Relay);
        _punch.StartRelayProbeLoop();
    }

    public async Task HandleErrorAsync(ErrorMessage error)
    {
        OnStatusChanged?.Invoke($"Error: {error.Message}");
        if (error.Code == 404)
        {
            return;
        }

        await RequestRelayIfAllowedAsync();
    }

    // ─── Internal handler methods (also used by standalone router) ───

    private async Task HandleP2pUnhealthyAsync(string sessionId, string reason)
    {
        OnStatusChanged?.Invoke($"P2P unhealthy on session {sessionId}: {reason}");
        await RequestRelayIfAllowedAsync();
    }

    private async Task ProcessErrorAsync(ErrorMessage error)
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
        var client = _signalingConnectionLoop.Transport;
        if (client?.IsConnected != true || string.IsNullOrWhiteSpace(_options.TargetNodeId))
        {
            return;
        }

        var request = new RelayRequest
        {
            TargetNodeId = _options.TargetNodeId,
            SessionId = _sessions.ActiveSessionId
        };

        await client.SendAsync(MessageSerializer.SerializeToString(request));
    }

    private void StartLanDiscoveryBroadcast()
    {
        var puncher = _punch.GetHolePuncher();
        if (puncher == null) return;

        var clientPort = puncher.LocalEndPoint?.Port ?? 0;
        var tempConv = (uint)Random.Shared.Next(100000, 99999999);
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                if (IsConnected)
                {
                    break;
                }

                if (_injectedUdpStack?.LanDiscovery is { } ld)
                {
                    await ld.BroadcastQueryAsync(_options.TargetNodeId, clientPort, tempConv);
                }
                else if (_lanDiscovery != null)
                {
                    await _lanDiscovery.BroadcastQueryAsync(_options.TargetNodeId, clientPort, tempConv);
                }
                await Task.Delay(100);
            }
        });
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
        _connectionRequestLock.Dispose();
        _punch.Dispose();

        if (_ownsSharedStacks)
        {
            // Standalone mode: we own all resources
            _signalingConnectionLoop.Dispose();
            _lanDiscovery?.Dispose();
        }
        else
        {
            // Shared-stack mode: unregister from dispatcher, don't dispose shared resources
            if (_injectedSignalingStack != null)
            {
                _injectedSignalingStack.Dispatcher.UnregisterNode(_options.TargetNodeId);
                // Unregister all sessions we own
                foreach (var sessionId in _sessions.SessionIds)
                {
                    _injectedSignalingStack.Dispatcher.UnregisterSession(sessionId);
                }
            }
        }

        _sessions.Dispose();
    }

    private async Task HandleSignalingConnectedAsync(SignalingTransportBase? transport)
    {
        if (_options.Role == PeerConnectionRole.Intranet)
        {
            await RegisterNodeAsync();
            OnStatusChanged?.Invoke("Ready");
            _punch.StartNatKeepAliveLoop(_cts.Token);
        }
        else
        {
            await RequestConnectionAsync(force: true);
        }
    }

    /// <summary>
    /// Returns a statistics snapshot for TUI dashboard or metrics export.
    /// </summary>
    public NegotiatorStats GetStatsSnapshot()
    {
        var sessionStats = _sessions.GetStatsSnapshot();
        return new NegotiatorStats(
            _sessions.Mode,
            _punch.NatDetection?.NatType.ToString() ?? "Unknown",
            _punch.PublicEndPoint?.ToString(),
            IsSignalingConnected,
            _sessions.SessionCount,
            _options.TargetNodeId,
            sessionStats.RateLimitBytesPerSec,
            sessionStats.TokenBucketUtilization,
            sessionStats.RttMs,
            sessionStats.Cwnd,
            sessionStats.WaitSnd,
            sessionStats.SentBytes,
            sessionStats.ReceivedBytes,
            sessionStats.InputErrors
        );
    }
}
