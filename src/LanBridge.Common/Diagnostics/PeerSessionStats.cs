using LanBridge.Common.Network;

namespace LanBridge.Common.Diagnostics;

/// <summary>
/// Statistics snapshot for a peer transport session.
/// Consumed by TUI dashboard or future HTTP metrics endpoint.
/// </summary>
public sealed record PeerSessionStats(
    PeerTransportMode Mode,
    long RttMs,
    uint Cwnd,
    int WaitSnd,
    long SentBytes,
    long ReceivedBytes,
    long SentPackets,
    long ReceivedPackets,
    long InputErrors,
    double TokenBucketUtilization,
    long RateLimitBytesPerSec
);

/// <summary>
/// Statistics snapshot for a ConnectionNegotiator.
/// </summary>
public sealed record NegotiatorStats(
    PeerTransportMode Mode,
    string NatType,
    string? PublicEndPoint,
    bool IsSignalingConnected,
    int ActiveSessionCount,
    string TargetNodeId
);
