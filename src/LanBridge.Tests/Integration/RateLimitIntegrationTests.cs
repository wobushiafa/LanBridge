using System.Diagnostics;
using LanBridge.Common.Network;
using Xunit;

namespace LanBridge.Tests.Integration;

/// <summary>
/// REQ-8: <see cref="PeerTransportSession.SetRateLimit"/> actually throttles
/// throughput, AND the runtime (<see cref="ConnectionNegotiator"/>) wires it from
/// <see cref="PeerConnectionOptions.RateLimitBytesPerSec"/>. Catches the "core
/// classes implemented but SetRateLimit never called from the runtime" bug class
/// (which went undetected by unit tests for an entire session).
/// </summary>
public class RateLimitIntegrationTests
{
    /// <summary>
    /// Behavior check: a directly-configured <see cref="PeerTransportSession"/>
    /// throttles 1 MB of MTU-sized frames to ~10s at 100 KB/s. The bucket check
    /// runs in <see cref="PeerTransportSession.ApplyRateLimitAsync"/> BEFORE
    /// <see cref="PeerTransportSession.SendCoreAsync"/>, so even with no KCP/relay
    /// session attached (SendCoreAsync is a no-op), the bucket still consumes tokens
    /// and throttles each frame.
    ///
    /// Uses 1 KB frames (not one 1 MB send) to mirror production's per-KCP-frame
    /// send pattern. A single 1 MB send would also throttle to ~10s (TokenBucket
    /// polls with a 1s cap per refill iteration; no deadlock for any finite length
    /// as long as rate > 0), but the frame-by-frame form is more representative.
    /// Token accumulation is tied to monotonic Stopwatch (wall time), so this is
    /// not machine-speed sensitive.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task SetRateLimit_ThrottlesSendThroughput()
    {
        using var session = new PeerTransportSession(verbose: false);
        session.SetRateLimit(new TokenBucket(100_000)); // 100 KB/s

        // Wiring check: the configured rate must be visible in stats, proving
        // SetRateLimit actually attached the bucket (catches a no-op SetRateLimit).
        var stats = session.GetStatsSnapshot();
        Assert.Equal(100_000, stats.RateLimitBytesPerSec);

        // Behavior check: if SendAsync/ApplyRateLimitAsync did not enforce the bucket,
        // all frames send instantly and elapsed ~0s, failing the InRange below.
        var data = new byte[1_000_000];
        const int frameSize = 1000; // 1 KB frames, well under the 10 KB burst
        var sw = Stopwatch.StartNew();
        for (int offset = 0; offset < data.Length; offset += frameSize)
        {
            int len = Math.Min(frameSize, data.Length - offset);
            await session.SendAsync(data, offset, len);
        }
        sw.Stop();

        // 1 MB / 100 KB/s ~= 10s. Allow generous band [8s, 15s] for polling-granularity overhead.
        Assert.InRange(sw.Elapsed.TotalSeconds, 8.0, 15.0);
    }

    /// <summary>
    /// Runtime wiring check: <see cref="ConnectionNegotiator"/>'s session factory
    /// (GetSession) must call <see cref="PeerTransportSession.SetRateLimit"/> when
    /// <see cref="PeerConnectionOptions.RateLimitBytesPerSec"/> is configured. This
    /// is the EXACT call site that was previously unwired (the historical bug). The
    /// negotiator is constructed WITHOUT calling StartAsync (no signaling loop, no
    /// STUN, no UDP) — GetSession runs lazily on first access and is independent of
    /// the connection lifecycle, so we can isolate the wiring cheaply (~instant,
    /// no 10s throttle wait). If GetSession stopped calling SetRateLimit, the stats
    /// would report RateLimitBytesPerSec == 0 and this test would fail.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Negotiator_WiresRateLimitFromConfig_WhenRateLimitConfigured()
    {
        var options = new PeerConnectionOptions
        {
            NodeId = "rate-wiring-node",
            TargetNodeId = "any-target",
            RateLimitBytesPerSec = 100_000,
        };
        using var negotiator = new ConnectionNegotiator(options);

        // GetStatsSnapshot() lazily creates the default session via GetSession("default"),
        // which is the factory that must wire the bucket from options.RateLimitBytesPerSec.
        var stats = negotiator.GetStatsSnapshot();

        Assert.Equal(100_000, stats.RateLimitBytesPerSec);
        await Task.CompletedTask; // keep async for xUnit Timeout support (no hang vector here)
    }
}
