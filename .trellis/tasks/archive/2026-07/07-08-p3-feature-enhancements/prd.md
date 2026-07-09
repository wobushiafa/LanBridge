# P3 功能增强：多隧道多目标、带宽QoS、统计TUI、WebSocket信令

## Goal

P3 阶段功能增强，包含 4 个独立子特性，提升 LanBridge 的多目标穿透能力、流量控制、可观测性和网络适应性。

## Sub-tasks

| # | 子任务 | 优先级 | 依赖 | 状态 |
|---|--------|--------|------|------|
| 13 | [多隧道多目标](../07-08-multi-tunnel-multi-target/prd.md) | P1 | 无 | planning |
| 14 | [带宽限制与QoS](../07-08-bandwidth-limit-qos/prd.md) | P1 | 无 | planning |
| 15 | [连接统计TUI面板](../07-08-stats-tui-dashboard/prd.md) | P2 | 14（消耗其 per-session 统计数据） | planning |
| 16 | [WebSocket信令传输](../07-08-websocket-signaling/prd.md) | P2 | 无 | planning |

## Cross-cutting Constraints

- 所有子任务必须兼容 Native AOT（零反射、Source Generator 序列化）
- 热数据路径保持零分配（ArrayPool、in-place header write）
- 所有功能向后兼容：不启用新配置时行为与 P2 一致
- `dotnet build -c Release` + `dotnet test` 必须始终通过

## Acceptance Criteria

- [ ] 子任务 13：ExtranetPeer 可同时连接 2+ 个 IntranetPeer，映射按 `@nodeId` 路由
- [ ] 子任务 14：per-session 带宽限速和 QoS 优先级可配置且生效
- [ ] 子任务 15：`--tui` 模式下实时显示会话、带宽、RTT、NAT、传输模式
- [ ] 子任务 16：`--signaling-transport ws` 可通过 WebSocket 连接信令服务器
- [ ] 所有子任务的默认配置下行为与 P2 一致
- [ ] `dotnet test` + `dotnet build -c Release` 通过

## Notes

- 子任务 15 依赖 14 的 per-session 统计数据，建议实施顺序：13 → 14 → 15 → 16
- 子任务 13 改动最大（架构重构），14/16 相对独立可并行
