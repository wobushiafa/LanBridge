using System.Net;
using System.Net.Sockets;
using LanBridge.Common.Protocol;

namespace LanBridge.Common.Network;

/// <summary>
/// Shared UDP infrastructure for multi-tunnel scenarios.
/// Owns the UdpHolePuncher (single socket, multi-remote KCP conv demux),
/// performs NAT detection once, and optionally starts LAN discovery.
/// </summary>
public sealed class SharedUdpStack : IDisposable
{
    private readonly UdpHolePuncher _holePuncher;
    private readonly PeerConnectionOptions _options;
    private LanDiscoveryService? _lanDiscovery;
    private NatDetectionResult? _natDetection;
    private bool _natDetected;

    public UdpHolePuncher HolePuncher => _holePuncher;
    public LanDiscoveryService? LanDiscovery => _lanDiscovery;
    public IPEndPoint? PublicEndPoint { get; private set; }
    public IPEndPoint? PublicEndPointV6 { get; private set; }
    public NatDetectionResult? NatDetection => _natDetection;

    public SharedUdpStack(PeerConnectionOptions options)
    {
        _options = options;
        _holePuncher = new UdpHolePuncher(options.UdpPort, options.NodeId);
    }

    /// <summary>
    /// Performs STUN-based NAT detection once. Results are cached and shared
    /// across all ConnectionNegotiator instances that use this stack.
    /// </summary>
    public async Task DetectNatAsync()
    {
        if (_natDetected)
        {
            return;
        }

        var diagnostics = new PeerNatDiagnostics(_options, status => { });
        var snapshot = await diagnostics.DetectAsync(_holePuncher);
        _natDetection = snapshot.Detection;
        PublicEndPoint = snapshot.PublicEndPoint;
        PublicEndPointV6 = snapshot.PublicEndPointV6;
        _natDetected = true;
    }

    /// <summary>
    /// Starts the LAN discovery service. Called once by the first Negotiator that needs it.
    /// </summary>
    public void StartLanDiscovery(ILanDiscoveryHost host)
    {
        if (_lanDiscovery != null)
        {
            return;
        }

        _lanDiscovery = new LanDiscoveryService(_options.NodeId, host, _options.Verbose);
        _lanDiscovery.Start();
    }

    public void Dispose()
    {
        _lanDiscovery?.Dispose();
        _holePuncher.Dispose();
    }
}
