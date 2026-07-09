# 连接统计实时 TUI 面板

## Goal

为 ExtranetPeer / IntranetPeer 增加可选的实时终端 UI 统计面板，展示活跃会话、上下行带宽、KCP RTT/丢包率、NAT 类型、传输模式等关键指标，提供类似 btop 的终端交互体验。

## Background

当前所有状态输出通过 `ConsoleStatusWriter` 静态辅助类和 `Console.Write` 实现，属于滚屏日志模式，无法实时聚合展示多维度数据。关键指标散落在：

- `OperationalTelemetry`：内存计数器（`signaling_clients_connected` 等）
- `PeerTransportSession._lastRttMs`：端到端 RTT（私有字段，仅 verbose 日志输出）
- `KcpSession.GetStats()`：KCP 统计快照（cwnd、waitSnd、sentBytes、recvBytes 等，仅在 `MaybeReportStats` 私有方法中调用）
- `ConnectionNegotiator`：Mode（P2P/Relay/None）、PublicEndPoint、NAT 类型

这些数据目前没有程序化暴露途径，TUI 需要一个统一的指标采集层。

## Requirements

### REQ-1: 指标采集接口

新增 `PeerStats` 快照记录类型，让 PeerTransportSession 和 ConnectionNegotiator 暴露结构化指标。TUI 通过轮询 `GetStats()` 快照采集数据，避免事件耦合。

### REQ-2: TUI 面板布局

`--tui` 或 `--dashboard` 启用后，终端切换为全屏 TUI 模式，替代滚屏日志。面板至少包含：

- **连接状态区**：传输模式（P2P/Relay/None）、NAT 类型、公网端点
- **会话列表区**：每个活跃会话的 StreamId、目标地址、协议、上下行字节/秒
- **KCP 指标区**：RTT、cwnd、waitSnd、丢包率（仅 P2P 模式）
- **累计统计区**：总上下行流量、活跃会话数、运行时长

### REQ-3: 刷新频率

TUI 面板每秒刷新一次，不造成明显 CPU 占用。

### REQ-4: 向后兼容

不启用 `--tui` 时，行为与当前完全一致（滚屏日志模式）。

## Acceptance Criteria

- [ ] `--tui` 启动后进入全屏终端面板模式
- [ ] 面板实时显示传输模式、NAT 类型、活跃会话数
- [ ] 面板实时显示上下行带宽（字节/秒）和累计流量
- [ ] P2P 模式下显示 KCP RTT 和 cwnd
- [ ] 不启用 `--tui` 时行为不变
- [ ] `dotnet build -c Release` + `dotnet test` 通过
- [ ] 兼容 Native AOT

## Out of Scope

- 图表/时间序列可视化
- 远程 Web dashboard
- 鼠标交互/窗口系统

## Resolved Decisions

- **D1: 使用 Spectre.Console 的 LiveDisplay 功能** — 兼容 Native AOT，API 简洁，支持表格/面板布局。备选 Terminal.Gui 过重且 AOT 兼容性差；自写 ANSI escape 太脆弱。
- **D2: 指标采集通过快照轮询模式** — 组件暴露 `GetStats()` 方法返回结构化快照，TUI 每秒轮询采集，避免事件耦合。

## Open Questions

_(None remaining)_
