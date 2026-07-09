using System.Net;
using System.Text;
using System.Collections.Concurrent;
using LanBridge.Common.Protocol;

namespace LanBridge.Common.Network;

/// <summary>
/// NAT / hole-punch subsystem extracted from <see cref="ConnectionNegotiator"/> (Phase 2).
/// Owns the <see cref="UdpHolePuncher"/>, pending-punch tracking, NAT detection state,
/// relay probe loop, and the unreliable (raw UDP) send path. Decoupled from the control
/// plane via event-out (status/mode/unreliable-data) and callback-in (signaling actions,
/// connectivity state, session attach).
/// </summary>
public sealed class PeerPunchCoordinator : IDisposable
{
    private readonly PeerConnectionOptions _options;
    private readonly PeerSessionManager _sessions;
    private readonly PeerNatDiagnostics _natDiagnostics;

    // Callbacks IN (control-plane / data-plane capabilities provided by the negotiator)
    private readonly Action<string, KcpSession> _attachP2p;
    private readonly Func<bool> _isConnected;
    private readonly Func<string> _activeSessionIdProvider;
    private readonly Func<bool> _isSignalingConnected;
    private readonly Func<PeerTransportMode> _modeProvider;
    private readonly Func<Task> _requestRelayAsync;
    private readonly Func<bool, Task> _requestConnectionAsync;
    private readonly Func<Task> _registerNodeAsync;

    // State (moved verbatim from ConnectionNegotiator)
    private UdpHolePuncher? _holePuncher;
    private readonly ConcurrentDictionary<string, PendingPunch> _pendingPunches = new();
    private IPEndPoint? _publicEndPoint;
    private IPEndPoint? _publicEndPointV6;
    private NatDetectionResult? _natDetection;
    private CancellationTokenSource? _relayProbeCts;
    private volatile bool _isHolePunching;
    private bool _isNatKeepAliveRunning;
    private bool _ownsHolePuncher;

    // Events OUT (forwarded to control/data plane by ConnectionNegotiator)
    public event Action<string>? OnStatusChanged;
    public event Action<PeerTransportMode>? OnModeChanged;
    public event Action<byte[], int, string>? OnUnreliableDataReceived;

    public IPEndPoint? PublicEndPoint => _publicEndPoint;
    public IPEndPoint? PublicEndPointV6 => _publicEndPointV6;
    public NatDetectionResult? NatDetection => _natDetection;

    public PeerPunchCoordinator(
        PeerConnectionOptions options,
        PeerSessionManager sessions,
        Action<string, KcpSession> attachP2p,
        Func<bool> isConnected,
        Func<string> activeSessionIdProvider,
        Func<bool> isSignalingConnected,
        Func<PeerTransportMode> modeProvider,
        Func<Task> requestRelayAsync,
        Func<bool, Task> requestConnectionAsync,
        Func<Task> registerNodeAsync)
    {
        _options = options;
        _sessions = sessions;
        _natDiagnostics = new PeerNatDiagnostics(options, status => OnStatusChanged?.Invoke(status));
        _attachP2p = attachP2p;
        _isConnected = isConnected;
        _activeSessionIdProvider = activeSessionIdProvider;
        _isSignalingConnected = isSignalingConnected;
        _modeProvider = modeProvider;
        _requestRelayAsync = requestRelayAsync;
        _requestConnectionAsync = requestConnectionAsync;
        _registerNodeAsync = registerNodeAsync;
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

    /// <summary>
    /// Builds / acquires the hole puncher, wires its events, and performs NAT detection.
    /// Called by <see cref="ConnectionNegotiator.StartAsync"/>. The signaling loop start
    /// and role-based branching remain in the negotiator.
    /// </summary>
    public async Task StartAsync(SharedUdpStack? injectedStack)
    {
        if (injectedStack != null)
        {
            // Shared-stack mode: use injected UdpHolePuncher and NAT detection
            _holePuncher = injectedStack.HolePuncher;
            _publicEndPoint = injectedStack.PublicEndPoint;
            _publicEndPointV6 = injectedStack.PublicEndPointV6;
            _natDetection = injectedStack.NatDetection;

            // If NAT detection hasn't been done yet, do it now (shared across all negotiators)
            await injectedStack.DetectNatAsync();
            _publicEndPoint = injectedStack.PublicEndPoint;
            _publicEndPointV6 = injectedStack.PublicEndPointV6;
            _natDetection = injectedStack.NatDetection;

            ConfigureHolePuncherEvents();
            _ownsHolePuncher = false;
        }
        else
        {
            // Standalone mode: create and own the hole puncher
            _holePuncher = new UdpHolePuncher(_options.UdpPort, _options.NodeId);
            ConfigureHolePuncherEvents();
            await DetectNatAsync();
            _ownsHolePuncher = true;
        }
    }

    public UdpHolePuncher? GetHolePuncher() => _holePuncher;

    public async Task DetectNatAsync()
    {
        var snapshot = await _natDiagnostics.DetectAsync(_holePuncher!);
        _natDetection = snapshot.Detection;
        _publicEndPoint = snapshot.PublicEndPoint;
        _publicEndPointV6 = snapshot.PublicEndPointV6;
    }

    public void StartNatKeepAliveLoop(CancellationToken cancellationToken)
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
            cancellationToken,
            shouldProbe: () => _isSignalingConnected() && !_isConnected() && !_isHolePunching,
            currentPublicEndPointProvider: () => _publicEndPoint,
            onMappingChangedAsync: async endpoint =>
            {
                _publicEndPoint = endpoint;
                await _registerNodeAsync();
            }).ContinueWith(_ =>
        {
            _isNatKeepAliveRunning = false;
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    public void StartRelayProbeLoop()
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
            while (!token.IsCancellationRequested && _modeProvider() == PeerTransportMode.Relay)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                    if (token.IsCancellationRequested || _modeProvider() != PeerTransportMode.Relay)
                    {
                        break;
                    }

                    OnStatusChanged?.Invoke("Relay is active. Probing whether P2P can be restored...");
                    await _requestConnectionAsync(true);
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

    /// <summary>
    /// Abandon an in-progress hole punch (e.g. relay has taken over). Mirrors the
    /// <c>_isHolePunching = false</c> assignment that previously lived in
    /// <see cref="ConnectionNegotiator.HandleRelayAcceptAsync"/>.
    /// </summary>
    public void StopHolePunching()
    {
        _isHolePunching = false;
    }

    public async Task<bool> EnsureConnectedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_isConnected())
        {
            return true;
        }

        if (_options.Role == PeerConnectionRole.Extranet)
        {
            await _requestConnectionAsync(false);
        }

        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_isConnected())
            {
                return true;
            }

            await Task.Delay(100, cancellationToken);
        }

        return _isConnected();
    }

    public async Task SendUnreliableAsync(byte[] data, int offset, int length)
    {
        await SendUnreliableAsync(_activeSessionIdProvider(), data, offset, length);
    }

    public async Task SendUnreliableAsync(string sessionId, byte[] data, int offset, int length)
    {
        var session = _sessions.GetSession(sessionId);
        var puncher = GetHolePuncher();
        if (session.Mode == PeerTransportMode.P2pDirect && puncher?.RemoteEndPoint != null)
        {
            try
            {
                // D4 (bandwidth-qos): raw UDP out-of-band path must apply the session's
                // token bucket before the raw socket send. Zero overhead when no bucket.
                await session.ApplyRateLimitAsync(length, CancellationToken.None);
                await puncher.Client.SendAsync(new ReadOnlyMemory<byte>(data, offset, length), puncher.RemoteEndPoint);
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

    public async Task HandleLanDiscoveryRequestAsync(IPEndPoint clientEndPoint, uint conv)
    {
        var sessionId = _activeSessionIdProvider();

        // Register the client in pending punches so it gets picked up by MarkPunched
        _pendingPunches[GetAddressKey(clientEndPoint.Address)] = new PendingPunch(sessionId, conv);

        var puncher = GetHolePuncher();
        if (puncher != null)
        {
            OnStatusChanged?.Invoke($"[LAN Discovery] Received local query from {clientEndPoint}. Replying advertisement & establishing direct KCP session...");

            // Reply with unicast advertisement containing target server port
            var serverPort = puncher.LocalEndPoint?.Port ?? 0;
            var advertiseMessage = $"LB_ADVERTISE:{_options.NodeId}:{serverPort}:{conv}";
            var data = Encoding.UTF8.GetBytes(advertiseMessage);
            await puncher.SendAsync(data, data.Length, clientEndPoint);

            // Directly trigger punch completion!
            puncher.TriggerHolePunched(clientEndPoint, conv);
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
            _attachP2p(pending.SessionId, session);
            if (pending.SessionId == _activeSessionIdProvider())
            {
                OnModeChanged?.Invoke(PeerTransportMode.P2pDirect);
            }
        };

        _holePuncher.OnLanAdvertised += (endpoint, conv) =>
        {
            if (_isConnected()) return;

            OnStatusChanged?.Invoke($"[LAN Discovery] Discovered local peer at {endpoint}, conv={conv}. Direct-connecting...");
            _isHolePunching = false;

            var sessionId = _activeSessionIdProvider();
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
                    OnUnreliableDataReceived?.Invoke(buffer, length, _activeSessionIdProvider());
                }
            }
        };
    }

    public string ExplainP2pFailure()
    {
        return P2pFailureExplainer.Describe(_natDetection);
    }

    public async Task BeginHolePunchFromSignalAsync(
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

    private async Task StartHolePunchAsync(string sessionId, uint conv, IPEndPoint? targetEndPoint, IPEndPoint? targetEndPointV6, bool allowRelayFallback, StunNatType targetNatType)
    {
        if (_sessions.GetSession(sessionId).Mode == PeerTransportMode.P2pDirect)
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
                if (_isHolePunching && !_isConnected())
                {
                    OnStatusChanged?.Invoke("Hole punch timeout. Falling back to relay...");
                    OnStatusChanged?.Invoke($"P2P failure reason: {ExplainP2pFailure()}");
                    await _requestRelayAsync();
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

    private async Task RequestRelayIfAllowedAsync()
    {
        if (_options.Role == PeerConnectionRole.Extranet && _options.EnableRelayFallback && _modeProvider() != PeerTransportMode.Relay)
        {
            OnStatusChanged?.Invoke("Falling back to relay mode...");
            await _requestRelayAsync();
        }
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
        _relayProbeCts?.Cancel();
        _relayProbeCts?.Dispose();
        if (_ownsHolePuncher)
        {
            _holePuncher?.Dispose();
        }
    }
}
