# Design: 连接统计实时 TUI 面板

## Architecture

新增 `LanBridge.Common.Tui` 命名空间，使用 Spectre.Console LiveDisplay 实现全屏终端面板。指标通过快照轮询从现有组件采集。

```
ExtranetPeer / IntranetPeer
  ├── PeerTransportSession → GetStats() → PeerSessionStats
  ├── ConnectionNegotiator → GetStats() → NegotiatorStats
  ├── OperationalTelemetry → Snapshot()
  └── TuiDashboard (新增, --tui 启用)
        └── Spectre.Console LiveDisplay
              ├── 连接状态区 (BreakdownChart / Panel)
              ├── 会话列表区 (Table)
              ├── KCP 指标区 (BarChart)
              └── 累计统计区 (Panel)
```

## Component Design

### 1. Stats 快照类型 (新增)

```csharp
namespace LanBridge.Common.Diagnostics;

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
    double TokenBucketUtilization
);

public sealed record NegotiatorStats(
    PeerTransportMode Mode,
    StunNatType NatType,
    string? PublicEndPoint,
    bool IsSignalingConnected,
    int ActiveSessionCount
);
```

### 2. PeerTransportSession 扩展

```csharp
// 新增公共方法
public PeerSessionStats GetStatsSnapshot()
{
    lock (_lock)
    {
        var kcpStats = _kcpSession?.GetStats();
        return new PeerSessionStats(
            Mode, _lastRttMs,
            kcpStats?.Cwnd ?? 0, kcpStats?.WaitSnd ?? 0,
            kcpStats?.SentBytes ?? 0, kcpStats?.ReceivedBytes ?? 0,
            kcpStats?.SentPackets ?? 0, kcpStats?.ReceivedPackets ?? 0,
            kcpStats?.InputErrors ?? 0,
            _tokenBucket?.Utilization ?? 0.0
        );
    }
}
```

### 3. ConnectionNegotiator 扩展

```csharp
// 新增公共方法
public NegotiatorStats GetStatsSnapshot()
{
    return new NegotiatorStats(
        Mode, _natDetection?.NatType ?? StunNatType.Unknown,
        _publicEndPoint?.ToString(), IsSignalingConnected,
        _sessions.Count
    );
}
```

### 4. TuiDashboard (新增)

```csharp
namespace LanBridge.Common.Tui;

public sealed class TuiDashboard : IDisposable
{
    private readonly Func<NegotiatorStats> _negotiatorStats;
    private readonly Func<IReadOnlyList<(uint StreamId, string Target, string Protocol, PeerSessionStats Stats)>> _sessionStats;
    private readonly Func<IReadOnlyDictionary<string, long>> _telemetrySnapshot;
    private readonly DateTime _startTimeUtc;

    public TuiDashboard(
        Func<NegotiatorStats> negotiatorStats,
        Func<IReadOnlyList<(uint, string, string, PeerSessionStats)>> sessionStats,
        Func<IReadOnlyDictionary<string, long>> telemetrySnapshot);

    public async Task RunAsync(CancellationToken ct);
    // 内部使用 Spectre.Console.AnsiConsole.Live() 每秒刷新
}
```

### 5. ExtranetPeer 集成

```csharp
// ExtranetPeer 构造时
if (_config.Transport.EnableTui)
{
    _dashboard = new TuiDashboard(
        () => _connection.GetStatsSnapshot(),
        () => GetActiveSessionStats(),
        () => _telemetry.Snapshot()
    );
}

// StartAsync 中
if (_dashboard != null)
{
    _ = _dashboard.RunAsync(_cts.Token);  // 后台渲染
}
```

### 6. 配置

**TransportOptions**：
```csharp
public bool EnableTui { get; set; }  // 新增
```

**命令行**：`--tui` 或 `--dashboard`

**JSON**：
```json
{ "transport": { "enableTui": true } }
```

## TUI Layout

```
┌───────────────────── LanBridge ─────────────────────┐
│ TRANSPORT: P2P DIRECT    NAT: PortRestrictedCone    │
│ Public: 203.0.113.5:12345    Signaling: Connected   │
├─────────────── Active Sessions ─────────────────────┤
│ StreamId  Target              Proto  ↑KB/s  ↓KB/s  │
│ 0xA3F2    192.168.7.230:554  TCP    1250   890     │
│ 0xB7C1    192.168.7.230:9999 UDP    45     52      │
├─────────────── KCP Transport ───────────────────────┤
│ RTT: 23ms    CWND: 256    WaitSnd: 2    Loss: 0.1%  │
├─────────────── Cumulative ──────────────────────────┤
│ ↑ Total: 14.2 MB    ↓ Total: 12.8 MB               │
│ Uptime: 01:23:45    Sessions: 2                     │
└─────────────────────────────────────────────────────┘
```

## Dependency

Spectre.Console NuGet 包 — 纯 C# 实现，兼容 Native AOT，支持 `LiveDisplay`、`Table`、`Panel`、`BarChart` 等组件。

## Compatibility

- 不启用 `--tui` 时，`TuiDashboard` 不创建，Console.Write 行为不变
- Stats 快照方法即使不启用 TUI 也可用（为未来 HTTP metrics 端点预留）
- Native AOT 兼容：Spectre.Console 使用 Source Generator 处理 JSON
