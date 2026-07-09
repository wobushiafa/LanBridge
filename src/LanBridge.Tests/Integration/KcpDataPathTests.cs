using System.Diagnostics;
using System.Text;
using LanBridge.Common.Network;
using Xunit;

namespace LanBridge.Tests.Integration;

/// <summary>
/// P2P KCP data-path integration tests — the highest-risk previously-untested path.
/// Exercises the real chain <c>KcpSession.Send → KCP → UDP → peer receive loop →
/// InputPacket → OnDataReceived</c> over loopback (no real hole-punch, no NAT, no
/// admin rights). Covers: KCP roundtrip (REQ-1), PeerTransportSession routing data
/// through KCP (REQ-2), rate-limit throttling on the real KCP path (REQ-3), and KCP
/// fragmentation/reassembly (REQ-4).
/// </summary>
public class KcpDataPathTests
{
    /// <summary>
    /// REQ-1: two KcpSessions over loopback exchange data both ways. Verifies the
    /// KCP protocol layer (3-way handshake, framing, delivery) actually moves bytes
    /// end-to-end. Send triggers the handshake; the reassembled data arrives a few
    /// hundred ms later, so we await the receive TaskCompletionSource with a generous
    /// 5s timeout rather than polling IsConnected.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Kcp_Loopback_RoundtripsData()
    {
        using var pair = new KcpLoopbackPair();
        var payload = Encoding.UTF8.GetBytes("hello kcp");
        var recvA = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recvB = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.SessionA.OnDataReceived += (d, n) => recvA.TrySetResult(d[..n]);
        pair.SessionB.OnDataReceived += (d, n) => recvB.TrySetResult(d[..n]);
        pair.Start();

        pair.SessionA.Send(payload, 0, payload.Length);
        pair.SessionB.Send(payload, 0, payload.Length);

        var gotB = await recvB.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var gotA = await recvA.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(payload, gotB);
        Assert.Equal(payload, gotA);
    }

    /// <summary>
    /// REQ-2: <see cref="PeerTransportSession"/> routes application payloads through a
    /// real KCP session. Verifies the data-plane facade (<c>SendAsync →
    /// ApplyRateLimitAsync → SendCoreAsync → kcp.Send</c>) and the event-forwarding
    /// chain (<c>KcpSession.OnDataReceived → PeerTransportSession.HandleP2pData →
    /// OnDataReceived</c>). Uses a raw non-TunnelFrame payload:
    /// <see cref="PeerTransportSession.HandleP2pData"/> only intercepts TunnelFrames
    /// with StreamId==0 Ping/Pong; everything else (including a TryDecode failure on
    /// raw bytes) falls through to <c>OnDataReceived</c>. UseP2p starts the KCP
    /// session internally, so Start() is NOT called on the pair.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task PeerTransport_RoutesDataThroughKcp()
    {
        using var pair = new KcpLoopbackPair();
        using var peerA = new PeerTransportSession(verbose: false);
        using var peerB = new PeerTransportSession(verbose: false);
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        peerB.OnDataReceived += (d, n) => tcs.TrySetResult(d[..n]);
        peerA.UseP2p(pair.SessionA);
        peerB.UseP2p(pair.SessionB);

        var payload = new byte[500];
        Random.Shared.NextBytes(payload);
        await peerA.SendAsync(payload, 0, payload.Length);

        var got = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(payload, got);
    }

    /// <summary>
    /// REQ-3: rate-limit throttles throughput on the REAL KCP path (not the no-op
    /// SendCoreAsync exercised by <see cref="RateLimitIntegrationTests"/>). The bucket
    /// check runs in <see cref="PeerTransportSession.ApplyRateLimitAsync"/> BEFORE
    /// <see cref="PeerTransportSession.SendCoreAsync"/>→<c>kcp.Send</c>, so 1 MB of
    /// 1 KB frames at 100 KB/s takes ~10s. We additionally assert the peer actually
    /// received bytes — proving data really traversed KCP (a no-op SendCoreAsync would
    /// pass the timing check but deliver nothing).
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task P2pPath_RateLimitThrottles()
    {
        using var pair = new KcpLoopbackPair();
        using var peerA = new PeerTransportSession(verbose: false);
        using var peerB = new PeerTransportSession(verbose: false);
        peerA.UseP2p(pair.SessionA);
        peerB.UseP2p(pair.SessionB);
        peerA.SetRateLimit(new TokenBucket(100_000)); // 100 KB/s

        long deliveredBytes = 0;
        peerB.OnDataReceived += (_, n) => Interlocked.Add(ref deliveredBytes, n);

        var data = new byte[1_000_000];
        const int frameSize = 1000;
        var sw = Stopwatch.StartNew();
        for (int offset = 0; offset < data.Length; offset += frameSize)
        {
            int len = Math.Min(frameSize, data.Length - offset);
            await peerA.SendAsync(data, offset, len);
        }
        sw.Stop();

        // 1 MB / 100 KB/s ≈ 10s. [8s, 15s] absorbs polling-granularity overhead.
        Assert.InRange(sw.Elapsed.TotalSeconds, 8.0, 15.0);

        // Give KCP a brief moment to flush the tail it already queued during the
        // throttled send, then prove bytes actually flowed over the wire.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.True(Interlocked.Read(ref deliveredBytes) > 0,
            "Rate-limit timed correctly but no data reached the peer — SendCoreAsync was effectively a no-op, " +
            "so this did NOT exercise the real KCP path.");
    }

    /// <summary>
    /// REQ-4: a message larger than one MTU is fragmented by the sender and reassembled
    /// by the receiver, arriving bit-for-bit identical. Uses 60000 bytes (~51 KCP
    /// fragments at MSS 1176), which is comfortably above the MTU (exercises
    /// fragmentation) but below the 65536-byte receive buffer that
    /// <see cref="KcpSession.InputPacket"/> rents — KCP's <c>Receive</c> returns -2
    /// (buffer too small) for any message larger than that, so a 100 KB payload would
    /// never be delivered. 10s timeout covers handshake + ~51-fragment transfer.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Kcp_FragmentsAndReassemblesLargeMessage()
    {
        using var pair = new KcpLoopbackPair();
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.SessionB.OnDataReceived += (d, n) => tcs.TrySetResult(d[..n]);
        pair.Start();

        var payload = new byte[60_000];
        Random.Shared.NextBytes(payload);
        pair.SessionA.Send(payload, 0, payload.Length);

        var got = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(payload.Length, got.Length);
        Assert.True(payload.SequenceEqual(got));
    }

    /// <summary>
    /// Regression guard for the 64KB-receive-buffer cap: a single KCP message of
    /// exactly 65536 bytes (the KcpSession.InputPacket receive buffer size) must
    /// be delivered intact. The data path (ExtranetPeer/IntranetPeer) caps TCP
    /// reads so a framed message never exceeds this 65536 limit (16-byte header +
    /// ≤65520 payload). If the receive buffer were shrunk below 65536, this fails
    /// with -2 (buffer too small); if the send-side cap were reverted (allowing
    /// >65536-byte messages), a peer read burst would silently drop. This test
    /// pins the receive-side half of that invariant.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Kcp_DeliversMaxSafeMessage_65536Bytes()
    {
        using var pair = new KcpLoopbackPair();
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.SessionB.OnDataReceived += (d, n) => tcs.TrySetResult(d[..n]);
        pair.Start();

        var payload = new byte[65536];
        Random.Shared.NextBytes(payload);
        pair.SessionA.Send(payload, 0, payload.Length);

        var got = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(65536, got.Length);
        Assert.True(payload.SequenceEqual(got));
    }
}
