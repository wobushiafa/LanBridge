using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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
}

public sealed class ConnectionNegotiator : IDisposable
{
    private readonly PeerConnectionOptions _options;
    private readonly ConcurrentDictionary<string, PeerTransportSession> _sessions = new();
    private readonly ConcurrentDictionary<string, PendingPunch> _pendingPunches = new();
    private readonly SemaphoreSlim _connectionRequestLock = new(1, 1);
    private SignalingClient? _signalingClient;
    private UdpHolePuncher? _holePuncher;
    private IPEndPoint? _publicEndPoint;
    private NatDetectionResult? _natDetection;
    private CancellationTokenSource? _relayProbeCts;
    private bool _isHolePunching;
    private string _activeSessionId = "default";
    private DateTime _lastConnectionRequestUtc = DateTime.MinValue;
    private readonly CancellationTokenSource _cts = new();

    public event Action<string>? OnStatusChanged;
    public event Action<byte[], int>? OnDataReceived;
    public event Action<string, byte[], int>? OnSessionDataReceived;
    public event Action<PeerTransportMode>? OnModeChanged;
    public event Action? OnSignalingDisconnected;

    public IPEndPoint? PublicEndPoint => _publicEndPoint;
    public PeerTransportMode Mode => GetSession(_activeSessionId).Mode;
    public bool IsConnected => GetSession(_activeSessionId).IsConnected;
    public bool IsSignalingConnected => _signalingClient?.IsConnected == true;

    public ConnectionNegotiator(PeerConnectionOptions options)
    {
        _options = options;
    }

    private sealed record PendingPunch(string SessionId, uint Conv);

    public async Task StartAsync()
    {
        _holePuncher = new UdpHolePuncher(_options.UdpPort, _options.NodeId);
        ConfigureHolePuncherEvents();

        await DetectNatAsync();
        await ConnectToSignalingServerAsync();

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

    private void StartNatKeepAliveLoop()
    {
        if (_options.Role != PeerConnectionRole.Intranet)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(25));
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                if (_cts.IsCancellationRequested)
                {
                    break;
                }

                if (!IsSignalingConnected)
                {
                    continue;
                }

                if (IsConnected || _isHolePunching)
                {
                    continue;
                }

                try
                {
                    var result = await StunClient.QueryAsync(
                        _holePuncher!,
                        _options.StunServerHost,
                        _options.StunServerPort,
                        timeoutMs: 2000);
                        
                    if (result.PublicEndPoint != null && !result.PublicEndPoint.Equals(_publicEndPoint))
                    {
                        OnStatusChanged?.Invoke($"NAT mapping changed from {_publicEndPoint} to {result.PublicEndPoint}. Re-registering...");
                        _publicEndPoint = result.PublicEndPoint;
                        await RegisterNodeAsync();
                    }
                }
                catch (Exception ex)
                {
                    if (_options.Verbose)
                    {
                        OnStatusChanged?.Invoke($"NAT keep-alive failed: {ex.Message}");
                    }
                }
            }
        }, _cts.Token);
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
        if (_options.Role != PeerConnectionRole.Extranet)
        {
            return;
        }

        if (_signalingClient?.IsConnected != true)
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
                ClientEndPoint = _publicEndPoint?.ToString()
            };

            await _signalingClient.SendAsync(MessageSerializer.SerializeToString(request));
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
        OnStatusChanged?.Invoke("Getting public endpoint via standard STUN...");
        OnStatusChanged?.Invoke($"STUN server: {_options.StunServerHost}:{_options.StunServerPort}");
        var holePuncher = _holePuncher!;
        OnStatusChanged?.Invoke($"Local endpoint: {holePuncher.LocalEndPoint}");

        var result = await StunClient.DetectNatAsync(
            holePuncher,
            _options.StunServerHost,
            _options.StunServerPort,
            _options.StunAlternateServerPort);

        _natDetection = result;
        _publicEndPoint = result.PublicEndPoint;
        if (_publicEndPoint == null)
        {
            OnStatusChanged?.Invoke($"STUN unavailable, continuing with relay fallback: {result.Reason}");
            return;
        }

        var mapping = result.PortPreserved ? "port-preserved" : "port-mapped";
        OnStatusChanged?.Invoke($"Public endpoint: {_publicEndPoint}");
        OnStatusChanged?.Invoke($"NAT mapping: {holePuncher.LocalEndPoint} -> {_publicEndPoint} ({mapping})");
        OnStatusChanged?.Invoke($"NAT type: {FormatNatType(result.NatType)}");
        OnStatusChanged?.Invoke($"NAT diagnosis: {result.Reason}");
    }

    private static string FormatNatType(StunNatType natType)
    {
        return natType switch
        {
            StunNatType.FullCone => "Full Cone",
            StunNatType.RestrictedCone => "Restricted Cone",
            StunNatType.PortRestrictedCone => "Port Restricted Cone",
            StunNatType.Symmetric => "Symmetric",
            StunNatType.Blocked => "Blocked",
            _ => "Unknown"
        };
    }

    private async Task ConnectToSignalingServerAsync()
    {
        OnStatusChanged?.Invoke("Connecting to signaling server...");

        _signalingClient = new SignalingClient(_options.SignalingServerHost, _options.SignalingServerPort);
        _signalingClient.OnMessageReceived += HandleSignalingMessage;
        _signalingClient.OnDisconnected += () =>
        {
            OnSignalingDisconnected?.Invoke();
            OnStatusChanged?.Invoke("Disconnected from signaling server");
        };
        _signalingClient.OnError += error => OnStatusChanged?.Invoke($"Signaling error: {error}");

        await _signalingClient.ConnectAsync();
        OnStatusChanged?.Invoke("Connected to signaling server");
    }

    private async Task RegisterNodeAsync()
    {
        var register = new RegisterMessage
        {
            NodeId = _options.NodeId,
            Token = _options.Token,
            PublicEndPoint = _publicEndPoint?.ToString()
        };

        await _signalingClient!.SendAsync(MessageSerializer.SerializeToString(register));
    }

    private async void HandleSignalingMessage(string message)
    {
        var baseMessage = MessageSerializer.Deserialize(message);
        if (baseMessage == null)
        {
            return;
        }

        switch (baseMessage.Type)
        {
            case MessageType.RegisterAck:
                var ack = (RegisterAck)baseMessage;
                OnStatusChanged?.Invoke($"Registration {(ack.Success ? "success" : "failed")}: {ack.Message}");
                break;

            case MessageType.ConnectReady:
                await HandleConnectReadyAsync((ConnectReady)baseMessage);
                break;

            case MessageType.HolePunchStart:
                await HandleHolePunchStartAsync((HolePunchStart)baseMessage);
                break;

            case MessageType.RelayAccept:
                await HandleRelayAcceptAsync((RelayAccept)baseMessage);
                break;

            case MessageType.Error:
                await HandleErrorAsync((ErrorMessage)baseMessage);
                break;
        }
    }

    private async Task HandleConnectReadyAsync(ConnectReady message)
    {
        var sessionId = string.IsNullOrWhiteSpace(message.SessionId) ? _activeSessionId : message.SessionId;
        _activeSessionId = sessionId;
        var target = _options.Role == PeerConnectionRole.Intranet
            ? message.ExtranetEndPoint
            : message.IntranetEndPoint;

        if (string.IsNullOrWhiteSpace(target))
        {
            OnStatusChanged?.Invoke("No remote UDP endpoint available. Falling back to relay...");
            await RequestRelayIfAllowedAsync();
            return;
        }

        OnStatusChanged?.Invoke($"Connection ready. Starting hole punch to {target}");
        await StartHolePunchAsync(sessionId, message.Conv, IPEndPoint.Parse(target), allowRelayFallback: _options.Role == PeerConnectionRole.Extranet);
    }

    private async Task HandleHolePunchStartAsync(HolePunchStart message)
    {
        var sessionId = string.IsNullOrWhiteSpace(message.SessionId) ? Guid.NewGuid().ToString("N")[..8] : message.SessionId;
        if (string.IsNullOrWhiteSpace(message.TargetEndPoint))
        {
            OnStatusChanged?.Invoke("No target UDP endpoint available. Falling back to relay...");
            await RequestRelayIfAllowedAsync();
            return;
        }

        OnStatusChanged?.Invoke($"Hole punch request to {message.TargetEndPoint}");
        await StartHolePunchAsync(sessionId, message.Conv, IPEndPoint.Parse(message.TargetEndPoint), allowRelayFallback: _options.Role == PeerConnectionRole.Extranet);
    }

    private async Task StartHolePunchAsync(string sessionId, uint conv, IPEndPoint targetEndPoint, bool allowRelayFallback)
    {
        if (GetSession(sessionId).Mode == PeerTransportMode.P2pDirect)
        {
            return;
        }

        _isHolePunching = true;
        _pendingPunches[targetEndPoint.Address.ToString()] = new PendingPunch(sessionId, conv);
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

        await _holePuncher!.StartPunchingAsync(targetEndPoint, 200, _options.HolePunchTimeoutMs);
    }

    private void ConfigureHolePuncherEvents()
    {
        _holePuncher!.OnHolePunched += endpoint =>
        {
            OnStatusChanged?.Invoke($"Hole punched to {endpoint}");
            _isHolePunching = false;

            if (!_pendingPunches.TryRemove(endpoint.Address.ToString(), out var pending))
            {
                OnStatusChanged?.Invoke($"Hole punched endpoint has no pending session: {endpoint}");
                _holePuncher.RemovePunchedEndpoint(endpoint);
                return;
            }

            var conv = pending.Conv != 0 ? pending.Conv : P2PConv.FromEndpoints(_publicEndPoint!, endpoint);
            OnStatusChanged?.Invoke($"P2P conv: {conv}");
            _relayProbeCts?.Cancel();
            var session = new KcpSession(conv, _holePuncher.Client, endpoint, ownReceiveLoop: false);
            _holePuncher.RegisterKcpSession(session);
            GetSession(pending.SessionId).UseP2p(session);
            if (pending.SessionId == _activeSessionId)
            {
                OnModeChanged?.Invoke(PeerTransportMode.P2pDirect);
            }
        };

        _holePuncher.OnError += error =>
        {
            OnStatusChanged?.Invoke($"Hole punch error: {error}");
            if (error.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            {
                OnStatusChanged?.Invoke($"P2P failure reason: {ExplainP2pFailure()}");
            }
        };
    }

    private string ExplainP2pFailure()
    {
        if (_natDetection == null)
        {
            return "NAT was not detected because STUN did not complete; relay is the safest fallback";
        }

        return _natDetection.NatType switch
        {
            StunNatType.Symmetric => "Symmetric NAT changed the public mapping across STUN endpoints, so peer-predicted UDP ports are unreliable; use relay/TURN-like fallback",
            StunNatType.PortRestrictedCone => "Port-restricted filtering likely blocked packets from the peer until both sides punch at the same time; retry may work, relay is expected fallback",
            StunNatType.Blocked => "STUN binding failed, so UDP traversal is blocked or unreachable; relay is required",
            StunNatType.Unknown => $"NAT type is unknown: {_natDetection.Reason}; relay fallback is recommended",
            StunNatType.RestrictedCone => "NAT looked traversal-friendly, so failure is likely firewall filtering, endpoint mismatch, packet loss, or peer offline",
            StunNatType.FullCone => "NAT looked traversal-friendly, so failure is likely firewall filtering, endpoint mismatch, packet loss, or peer offline",
            _ => _natDetection.Reason
        };
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
        if (_signalingClient?.IsConnected != true || string.IsNullOrWhiteSpace(_options.TargetNodeId))
        {
            return;
        }

        var request = new RelayRequest
        {
            TargetNodeId = _options.TargetNodeId,
            SessionId = _activeSessionId
        };

        await _signalingClient.SendAsync(MessageSerializer.SerializeToString(request));
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

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
        _connectionRequestLock.Dispose();
        _relayProbeCts?.Cancel();
        _relayProbeCts?.Dispose();
        _signalingClient?.Dispose();
        _holePuncher?.Dispose();
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();
    }
}
