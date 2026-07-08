# Design: 带宽限制与 QoS 优先级队列

## Architecture

在 `PeerTransportSession.SendAsync` 入口处插入令牌桶限速 + 优先级调度层，不修改 KcpSession 内部。

```
ExtranetPeer.HandleLocalClientAsync
  → mapping.RateLimitBytesPerSec 检查
  → TunnelRouter.GetNegotiatorForLocalPort(localPort)
  → ConnectionNegotiator.SendAsync(data)
  → PeerTransportSession.SendAsync(data)
      → TokenBucket.TryConsume(length)  ← 新增
      → 如果令牌不足 → await _tokenBucket.WaitForTokensAsync(length)
      → PriorityQueue.Enqueue(frame, priority)  ← 新增（仅当队列非空时排队）
      → KcpSession.Send / RelayClient.SendAsync
```

## Component Design

### 1. TokenBucket (新增)

```csharp
public sealed class TokenBucket
{
    private readonly double _rate;           // tokens per second
    private readonly double _burstCapacity;  // max tokens
    private double _tokens;
    private DateTime _lastRefillUtc;
    private readonly SemaphoreSlim _waitSignal = new(0);

    public TokenBucket(long rateBytesPerSec, long burstCapacityBytes);

    // 非阻塞尝试消耗，返回是否成功
    public bool TryConsume(int length);

    // 异步等待令牌补满后消耗
    public Task WaitForTokensAsync(int length, CancellationToken ct);

    // 补充令牌（内部定时调用或由 WaitForTokensAsync 驱动）
    private void Refill();
}
```

**零分配保证**：`WaitForTokensAsync` 使用 `SemaphoreSlim` 而非 `TaskCompletionSource`，无 per-await 堆分配。`TokenBucket` 实例在 TunnelMapping 级别创建，不在热路径上 new。

### 2. PriorityFrameQueue (新增)

```csharp
public enum FramePriority : byte { High = 0, Normal = 1, Low = 2 }

internal sealed class PriorityFrameQueue
{
    // 两个队列：High 优先，Normal+Low 共享
    private readonly ConcurrentQueue<PendingFrame> _highQueue = new();
    private readonly ConcurrentQueue<PendingFrame> _normalQueue = new();

    public void Enqueue(byte[] data, int offset, int length, FramePriority priority);
    public bool TryDequeue(out byte[] data, out int offset, out int length);
}
```

**设计**：双队列而非三队列，因为 Low 仅在拥塞时延迟——实现方式是 Normal 队列的 Low 元素排在 Normal 元素之后（通过内部排序或简单策略：High 队列空时才取 Normal 队列，拥塞时 Low 延迟 1 个周期）。

### 3. PeerTransportSession 扩展

```csharp
// 新增方法
public void SetRateLimit(TokenBucket bucket);
public void SetPriority(FramePriority priority);

// SendAsync 内部改造
public async Task SendAsync(byte[] data, int offset, int length)
{
    // 1. 令牌桶限速
    if (_tokenBucket != null && !_tokenBucket.TryConsume(length))
    {
        await _tokenBucket.WaitForTokensAsync(length, _sendCts.Token);
    }

    // 2. 优先级队列（仅当队列已有积压时才排队，避免无拥塞时的额外开销）
    if (!_priorityQueue.IsEmpty)
    {
        _priorityQueue.Enqueue(data, offset, length, _priority);
        await DrainQueueAsync();
        return;
    }

    // 3. 直接发送
    await SendCoreAsync(data, offset, length);
}
```

### 4. 配置扩展

**TunnelMapping**：
```csharp
public class TunnelMapping
{
    // ... existing fields ...
    public long? RateLimitBytesPerSec { get; set; }   // 新增，默认 null = 不限速
    public string? Priority { get; set; }              // 新增，"high"/"normal"/"low"，默认 "normal"
}
```

**命令行**：
```
-m 8554=192.168.7.230:554:tcp --rate-limit 1048576 --priority high
```

**JSON**：
```json
{
  "localPort": 8554,
  "targetHost": "192.168.7.230",
  "targetPort": 554,
  "protocol": "tcp",
  "rateLimitBytesPerSec": 1048576,
  "priority": "high"
}
```

### 5. 统计数据暴露

PeerTransportSession 增加快照方法供 TUI 消费：

```csharp
public sealed record PeerSessionStats(
    PeerTransportMode Mode,
    long RttMs,
    long SentBytes,
    long ReceivedBytes,
    long RateLimitBytesPerSec,      // 0 = 不限速
    double TokenBucketUtilization    // 0.0 ~ 1.0
);

public PeerSessionStats GetStats();
```

## Compatibility

- `RateLimitBytesPerSec = null`（默认）时，不创建 TokenBucket，SendAsync 路径无额外开销
- `Priority = "normal"`（默认）时，优先级队列为空不介入发送路径
- KcpSession 零改动，其拥塞控制窗口不受影响
- Native AOT 兼容：所有类型为 concrete class/record，无反射

## Rollback

不配置 rate-limit 和 priority 时，代码路径与改造前完全一致（TokenBucket 为 null、队列为空），零性能回归。
