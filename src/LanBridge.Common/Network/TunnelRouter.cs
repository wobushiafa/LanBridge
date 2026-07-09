using System.Collections.Concurrent;
using LanBridge.Common.Protocol;

namespace LanBridge.Common.Network;

/// <summary>
/// Routes local proxy connections to the correct ConnectionNegotiator
/// based on target node ID. Manages multiple ConnectionNegotiator instances
/// sharing a single UdpHolePuncher and SignalingConnectionLoop.
/// </summary>
public sealed class TunnelRouter : IDisposable
{
    private readonly SharedUdpStack _udpStack;
    private readonly SharedSignalingStack _signalingStack;
    private readonly ConcurrentDictionary<string, ConnectionNegotiator> _negotiators = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _localPortToNodeId;
    private readonly string _defaultNodeId;
    private readonly PeerConnectionOptions _baseOptions;
    private readonly IReadOnlyDictionary<string, (long RateLimitBytesPerSec, FramePriority Priority)> _nodeQos;
    private bool _started;

    public SharedUdpStack UdpStack => _udpStack;
    public SharedSignalingStack SignalingStack => _signalingStack;
    public IReadOnlyDictionary<string, ConnectionNegotiator> Negotiators => _negotiators;

    /// <param name="baseOptions">Base connection options (role, node ID, signaling host/port, STUN config, etc.)</param>
    /// <param name="localPortToNodeId">Mapping from local proxy port to target node ID.</param>
    /// <param name="defaultNodeId">Fallback target node ID when a mapping doesn't specify one.</param>
    /// <param name="nodeQos">Optional per-target-node QoS overrides (rate limit + priority). Keys are node IDs.</param>
    public TunnelRouter(
        PeerConnectionOptions baseOptions,
        Dictionary<int, string> localPortToNodeId,
        string defaultNodeId,
        IReadOnlyDictionary<string, (long RateLimitBytesPerSec, FramePriority Priority)>? nodeQos = null)
    {
        _baseOptions = baseOptions;
        _localPortToNodeId = localPortToNodeId;
        _defaultNodeId = defaultNodeId;
        _nodeQos = nodeQos ?? new Dictionary<string, (long, FramePriority)>(0, StringComparer.OrdinalIgnoreCase);

        // Create shared infrastructure (one UDP socket, one signaling TCP connection)
        _udpStack = new SharedUdpStack(baseOptions);
        _signalingStack = new SharedSignalingStack(
            baseOptions.SignalingServerHost,
            baseOptions.SignalingServerPort,
            status => { },
            baseOptions.SignalingTransport,
            baseOptions.SignalingWsPort);
    }

    /// <summary>
    /// Returns the set of unique target node IDs across all mappings.
    /// </summary>
    public IReadOnlySet<string> GetTargetNodeIds()
    {
        return _localPortToNodeId.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the target node ID for a given local port.
    /// Falls back to the default node ID if not explicitly mapped.
    /// </summary>
    public string GetNodeIdForLocalPort(int localPort)
    {
        return _localPortToNodeId.TryGetValue(localPort, out var nodeId) ? nodeId : _defaultNodeId;
    }

    /// <summary>
    /// Returns the ConnectionNegotiator responsible for a given target node.
    /// </summary>
    public ConnectionNegotiator? GetNegotiatorForNode(string nodeId)
    {
        return _negotiators.TryGetValue(nodeId, out var negotiator) ? negotiator : null;
    }

    /// <summary>
    /// Returns the ConnectionNegotiator that handles traffic for a given local port.
    /// </summary>
    public ConnectionNegotiator? GetNegotiatorForLocalPort(int localPort)
    {
        var nodeId = GetNodeIdForLocalPort(localPort);
        return GetNegotiatorForNode(nodeId);
    }

    /// <summary>
    /// Creates and starts a ConnectionNegotiator for each unique target node.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (_started)
        {
            return;
        }
        _started = true;

        // Perform NAT detection once (shared across all negotiators)
        await _udpStack.DetectNatAsync();

        // Start the shared signaling loop
        _ = Task.Run(() => _signalingStack.ConnectionLoop.RunAsync(ct), ct);

        // Create one ConnectionNegotiator per unique target node
        foreach (var nodeId in GetTargetNodeIds())
        {
            var (rateLimit, priority) = _nodeQos.TryGetValue(nodeId, out var qos)
                ? qos
                : (0L, _baseOptions.Priority);

            var options = _baseOptions with
            {
                TargetNodeId = nodeId,
                RateLimitBytesPerSec = rateLimit,
                Priority = priority
            };

            var negotiator = new ConnectionNegotiator(options, _udpStack, _signalingStack);
            _negotiators[nodeId] = negotiator;

            await negotiator.StartAsync();
        }
    }

    /// <summary>
    /// Sends reliable data to the appropriate tunnel based on which local port the data came from.
    /// </summary>
    public async Task SendToRemoteAsync(int localPort, byte[] data, int offset, int length)
    {
        var negotiator = GetNegotiatorForLocalPort(localPort);
        if (negotiator == null) return;

        if (!negotiator.IsConnected)
        {
            _ = negotiator.EnsureConnectedAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
        }

        await negotiator.SendAsync(data, offset, length);
    }

    public async Task SendHighPriorityToRemoteAsync(int localPort, byte[] data, int offset, int length)
    {
        var negotiator = GetNegotiatorForLocalPort(localPort);
        if (negotiator == null) return;

        if (!negotiator.IsConnected)
        {
            _ = negotiator.EnsureConnectedAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
        }

        await negotiator.SendHighPriorityAsync(data, offset, length);
    }

    /// <summary>
    /// Sends unreliable (UDP) data to the appropriate tunnel.
    /// </summary>
    public async Task SendUnreliableToRemoteAsync(int localPort, byte[] data, int offset, int length)
    {
        var negotiator = GetNegotiatorForLocalPort(localPort);
        if (negotiator == null) return;

        if (!negotiator.IsConnected)
        {
            _ = negotiator.EnsureConnectedAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
        }

        await negotiator.SendUnreliableAsync(data, offset, length);
    }

    public void Dispose()
    {
        foreach (var negotiator in _negotiators.Values)
        {
            negotiator.Dispose();
        }
        _negotiators.Clear();
        _udpStack.Dispose();
        _signalingStack.Dispose();
    }
}
