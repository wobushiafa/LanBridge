# Design: TUI 可观测性增强

## Architecture

`TuiDashboard` 从"单 negotiator 瞬时快照"变为"多隧道列表 + KCP 链路统计 + 实时吞吐率"。

```
ExtranetPeer
  └── TuiDashboard(nodeName, role,
        () => _router.Negotiators.Values.Select(n => n.GetStatsSnapshot()).ToList(),  ← 列表
        () => _telemetry.GetSnapshot())  ← 真实 telemetry（修空）
      内部: 跟踪上次 SentBytes/ReceivedBytes + 时间戳 → bytes/sec
```

## Component Design

### 1. NegotiatorStats 扩展（`PeerSessionStats.cs`）

```csharp
public sealed record NegotiatorStats(
    PeerTransportMode Mode,
    string NatType,
    string? PublicEndPoint,
    bool IsSignalingConnected,
    int ActiveSessionCount,
    string TargetNodeId,
    long RateLimitBytesPerSec,
    double TokenBucketUtilization,
    // 新增 KCP 字段（从 PeerSessionStats 映射）
    long RttMs,
    uint Cwnd,
    int WaitSnd,
    long SentBytes,
    long ReceivedBytes,
    long InputErrors);
```

`ConnectionNegotiator.GetStatsSnapshot()`（已调 `_sessions.GetStatsSnapshot()` 返回 `PeerSessionStats`）把新字段映射上去：
```csharp
var s = _sessions.GetStatsSnapshot();
return new NegotiatorStats(Mode, ..., s.RateLimitBytesPerSec, s.TokenBucketUtilization,
    s.RttMs, s.Cwnd, s.WaitSnd, s.SentBytes, s.ReceivedBytes, s.InputErrors);
```

### 2. TuiDashboard 改造

**ctor 签名**：
```csharp
public TuiDashboard(string nodeName, string role,
    Func<IReadOnlyList<NegotiatorStats>> tunnelsStats,
    Func<IReadOnlyDictionary<string, long>> telemetrySnapshot)
```

**吞吐率跟踪**（实例字段）：
```csharp
private DateTime _lastSnapshotUtc = DateTime.UtcNow;
private long _lastSentBytes, _lastRecvBytes;
// 每次 Render: var now = DateTime.UtcNow; var dt = (now - _lastSnapshotUtc).TotalSeconds;
// sentRate = (totalSent - _lastSentBytes) / dt; 更新 _last* = current; _lastSnapshotUtc = now;
```
（总计 = 所有隧道 SentBytes/ReceivedBytes 之和。）

**Render 布局**：
```
═══════════════════════════════════════════════
  LanBridge ExtranetPeer — <node>
═══════════════════════════════════════════════
  TRANSPORT: P2P DIRECT   NAT: Full Cone   Signaling: Connected
  Uptime: 01:23:45    Total: ↑ 12.4 MB/s  ↓ 8.1 MB/s

  Tunnels (3):
    Target           Mode      RTT    cwnd   ↑rate      ↓rate      sent      recv
    intranet-001     P2P       24ms   32     1.2MB/s    0.8MB/s    45MB      30MB
    intranet-002     Relay     —      —      0.5MB/s    0.3MB/s    10MB      6MB
    intranet-003     DISC      —      —      0          0          0         0
    (rate-limit: 1.0MB/s util 42% where configured)

  Telemetry:
    signaling_clients_connected: 3
    ...

  Press Ctrl+C to exit
```

- 多隧道行循环 `tunnelsStats()`。
- 单隧道（IntranetPeer）只一行。
- DISC 模式 RTT/cwnd 显示 `—`。
- 吞吐率人类可读（<1KB/s 显 B/s，<1MB/s 显 KB/s，否则 MB/s）。

### 3. ExtranetPeer 接线（`:275-286`）

```csharp
if (_config.Transport.EnableTui)
{
    _dashboard = new TuiDashboard(
        _config.Identity.NodeId,
        "ExtranetPeer",
        () => _router.Negotiators.Values.Select(n => n.GetStatsSnapshot()).ToList(),
        () => _telemetry.GetSnapshot());  // 修：真实 telemetry
}
```
**telemetry 快照**：`OperationalTelemetry` 需暴露计数 dict（`FormatSnapshot()` 返回格式化字符串——需加 `GetSnapshot()` 返回 `IReadOnlyDictionary<string,long>`，或 TUI 用 `FormatSnapshot()` 字符串直接显示）。**决策**：加 `OperationalTelemetry.GetSnapshot()` 返回 dict（若不存在）；若 counter 集合已暴露则直接用。

### 4. IntranetPeer TUI（stretch）
`IntranetPeer` 加 `--tui` flag（`TransportOptions.EnableTui` 已有）+ `TuiDashboard` 接线，列表单元素 `() => new[] { _connection.GetStatsSnapshot() }`。telemetry 用自己的 `OperationalTelemetry`（若有）或空。

## Compatibility

- `NegotiatorStats` 加字段：record 位置参数变多——核对所有构造点（`ConnectionNegotiator.GetStatsSnapshot` 是唯一构造点？+ 集成测试 `RateLimitIntegrationTests` 调 `GetStatsSnapshot()` 但不构造 NegotiatorStats）。加字段是 breaking for 位置参数构造，但唯一构造点在产品代码内，可控。
- `TuiDashboard` ctor 签名变：仅 ExtranetPeer 调用（IntranetPeer 若加也是新调用）。无外部破坏。
- `OperationalTelemetry.GetSnapshot()` 若新加：加法。

## Rollback

TUI 改动独立；stats record 加字段回退（删字段）；ExtranetPeer 接线回退。

## Native AOT

record + sealed class + ANSI 字符串，无反射。AOT 兼容。

## Risk

- **低**：纯展示层增强 + stats 加字段。集成测试不依赖 TUI（TUI 无测试）。
- 主要风险：吞吐率计算的并发/时序（`_last*` 字段单线程 Render 访问，无并发）。`NegotiatorStats` 构造点位置参数变化——核对唯一构造点。
