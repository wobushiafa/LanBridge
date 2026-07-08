using LanBridge.Common.Network;
using Xunit;

namespace LanBridge.Tests;

public class TokenBucketTests
{
    [Fact]
    public void TryConsume_SucceedsWhenTokensAvailable()
    {
        var bucket = new TokenBucket(1_000_000); // 1 MB/s
        Assert.True(bucket.TryConsume(100));
    }

    [Fact]
    public void TryConsume_FailsWhenBurstExhausted()
    {
        var bucket = new TokenBucket(1000); // 1 KB/s, burst = max(100, 1500) = 1500
        // Exhaust the burst capacity
        Assert.True(bucket.TryConsume(1500));
        // Next consume should fail
        Assert.False(bucket.TryConsume(1));
    }

    [Fact]
    public async Task WaitForTokensAsync_EventuallySucceeds()
    {
        var bucket = new TokenBucket(1_000_000); // 1 MB/s
        // Exhaust burst
        while (bucket.TryConsume(1000)) { }
        // Wait should succeed after refill
        await bucket.WaitForTokensAsync(100, CancellationToken.None);
    }

    [Fact]
    public void Utilization_DecreasesAfterConsume()
    {
        var bucket = new TokenBucket(1_000_000);
        var before = bucket.Utilization;
        Assert.True(before > 0.9);

        // Exhaust most tokens
        while (bucket.TryConsume(1000)) { }
        var after = bucket.Utilization;
        Assert.True(after < 0.1);
    }

    [Fact]
    public void RateBytesPerSec_ReturnsConfiguredRate()
    {
        var bucket = new TokenBucket(42_000_000);
        Assert.Equal(42_000_000, bucket.RateBytesPerSec);
    }
}

public class PriorityFrameQueueTests
{
    [Fact]
    public void HighPriority_DeqeuedBeforeNormal()
    {
        var queue = new PriorityFrameQueue();
        var normalData = new byte[] { 1 };
        var highData = new byte[] { 2 };

        queue.Enqueue(normalData, 0, 1, FramePriority.Normal);
        queue.Enqueue(highData, 0, 1, FramePriority.High);

        Assert.True(queue.TryDequeue(out var data, out _, out _));
        Assert.Equal(2, data[0]); // High first
        Assert.True(queue.TryDequeue(out data, out _, out _));
        Assert.Equal(1, data[0]); // Then normal
    }

    [Fact]
    public void IsEmpty_WhenNoFrames()
    {
        var queue = new PriorityFrameQueue();
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void TryDequeue_FailsWhenEmpty()
    {
        var queue = new PriorityFrameQueue();
        Assert.False(queue.TryDequeue(out _, out _, out _));
    }

    [Fact]
    public void Fifo_WithinSamePriority()
    {
        var queue = new PriorityFrameQueue();
        var data1 = new byte[] { 1 };
        var data2 = new byte[] { 2 };

        queue.Enqueue(data1, 0, 1, FramePriority.Normal);
        queue.Enqueue(data2, 0, 1, FramePriority.Normal);

        Assert.True(queue.TryDequeue(out var first, out _, out _));
        Assert.Equal(1, first[0]);
        Assert.True(queue.TryDequeue(out var second, out _, out _));
        Assert.Equal(2, second[0]);
    }
}

public class TunnelMappingPriorityTests
{
    [Fact]
    public void EffectivePriority_UDP_DefaultsToHigh()
    {
        var mapping = new LanBridge.ExtranetPeer.TunnelMapping
        {
            Protocol = "udp"
        };
        Assert.Equal(FramePriority.High, mapping.EffectivePriority);
    }

    [Fact]
    public void EffectivePriority_TCP_DefaultsToNormal()
    {
        var mapping = new LanBridge.ExtranetPeer.TunnelMapping
        {
            Protocol = "tcp"
        };
        Assert.Equal(FramePriority.Normal, mapping.EffectivePriority);
    }

    [Fact]
    public void EffectivePriority_ExplicitHighOverridesDefault()
    {
        var mapping = new LanBridge.ExtranetPeer.TunnelMapping
        {
            Protocol = "tcp",
            Priority = "high"
        };
        Assert.Equal(FramePriority.High, mapping.EffectivePriority);
    }
}
