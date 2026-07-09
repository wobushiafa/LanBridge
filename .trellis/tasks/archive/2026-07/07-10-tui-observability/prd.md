# TUI 可观测性增强

## Goal

把 `TuiDashboard` 从简陋的状态显示升级为有实际运维价值的面板：展示 KCP 链路健康（RTT/cwnd/字节）、多隧道列表、实时吞吐率。数据大多已存在（`PeerSessionStats`）但 TUI 根本没取；多隧道只显示第一个 negotiator；telemetry 回调是空的。

## Background

当前 `TuiDashboard`（113 行）只消费 `NegotiatorStats`（Mode/NAT/PublicEP/Signaling/Sessions/Rate+util）+ 前 8 个 telemetry 计数 + uptime。但：
- `PeerSessionStats` 有 **RTT、cwnd、WaitSnd、SentBytes、ReceivedBytes、SentPackets、ReceivedPackets、InputErrors**——关键 P2P 链路指标，TUI 完全没显示。
- ExtranetPeer 只传 `firstNegotiator.GetStatsSnapshot()`（`ExtranetPeer.cs:277`），多隧道时其他隧道不可见。
- telemetry 回调 `() => new Dictionary<string, long>()`（`:284`）是**空的**——telemetry 区啥也不显示。
- IntranetPeer 完全没有 TUI。
- 无吞吐率（bytes/sec）——只有累计字节。

## Scope

### REQ-1: 扩展 NegotiatorStats 含 KCP 字段
`NegotiatorStats` record 加 `RttMs`、`Cwnd`、`WaitSnd`、`SentBytes`、`ReceivedBytes`、`InputErrors`（从 `PeerSessionStats` 取）。`ConnectionNegotiator.GetStatsSnapshot()` 填充它们（已调 `_sessions.GetStatsSnapshot()`，把字段映射上去）。

### REQ-2: TuiDashboard 多隧道列表 + KCP 统计
- ctor 改 `Func<IReadOnlyList<NegotiatorStats>>`（列表，替代单个）。
- 渲染每隧道一行：TargetNodeId | Mode | RTT | cwnd | sent/recv bytes | rate-limit util。
- KCP 健康行：RTT/cwnd/WaitSnd/InputErrors（P2P 链路质量）。

### REQ-3: 实时吞吐率
TUI 内部跟踪上次 SentBytes/ReceivedBytes + 时间戳，每秒算 delta → bytes/sec（人类可读：KB/s、MB/s）。显示每隧道 + 总计的发送/接收速率。

### REQ-4: 修空 telemetry
ExtranetPeer 的 telemetry 回调 `() => new Dictionary<>()` 接到真实 `OperationalTelemetry` 快照（`Telemetry.FormatSnapshot()` 已存在，或暴露计数 dict）。

### REQ-5: IntranetPeer TUI（stretch）
IntranetPeer 加 `--tui` + `TuiDashboard` 接线（对称）。单 negotiator，列表只一行。

## Acceptance Criteria

- [ ] `NegotiatorStats` 含 KCP 字段（RTT/cwnd/WaitSnd/SentBytes/ReceivedBytes/InputErrors）
- [ ] TUI 显示每隧道一行（多隧道列表）
- [ ] TUI 显示 KCP 链路统计（RTT/cwnd/bytes）
- [ ] TUI 显示实时吞吐率（bytes/sec，每隧道 + 总计）
- [ ] ExtranetPeer telemetry 回调接真实数据（非空）
- [ ] IntranetPeer 支持 `--tui`（stretch，若做）
- [ ] `dotnet build -c Release` 0 警告，`dotnet test` 全绿（69，无回归）
- [ ] Native AOT 兼容（无反射；ANSI 转义不变）

## Out of Scope

- HTTP metrics 端点（`PeerSessionStats` 注释提到"future HTTP metrics endpoint"，本任务只 TUI）
- 图表/历史趋势（只实时快照）
- TUI 交互（按键切换视图等）——只读面板
- 颜色主题/布局大改——在现有 ANSI 风格上扩展

## Resolved Decisions

- **D1: 扩展 NegotiatorStats 而非新 record**——它是 TUI 的数据源，加字段是加法、安全；避免再引入一个 record。
- **D2: ctor 改列表签名**——仅 ExtranetPeer 调用（IntranetPeer 若加也是单元素列表），无外部破坏。
- **D3: 吞吐率在 TUI 内算**——跟踪上次快照 delta，不污染产品 stats record（保持 stats 是瞬时快照）。

## Open Questions

- IntranetPeer TUI 是否值得做（REQ-5）？倾向做（对称、低成本），但可降级为 stretch。
