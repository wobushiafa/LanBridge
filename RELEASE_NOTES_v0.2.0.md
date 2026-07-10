# LanBridge v0.2.0

本次版本重点补齐了信令传输、可观测性、多隧道路由、QoS/限速和测试基础设施，让项目从单点能力验证进一步走向可持续迭代。

## Highlights

- 新增 WebSocket 信令传输，支持 `tcp`、`ws` 和 `auto` 三种模式，适合受限网络环境。
- 新增多隧道多目标路由，单台 ExtranetPeer 可以同时连接多个 IntranetPeer。
- 新增 TUI 实时统计仪表盘，展示连接状态、传输模式、吞吐与 KCP 统计。
- 带宽限速与 QoS 优先级已接入运行时，可按目标节点生效。
- 增加了成体系的集成测试，并修复了 WebSocket 释放阶段的超时问题。
- 配置校验与 CLI 错误提示更严格，非法 `signalingTransport` / `wsPort` 会直接报错。

## Added

- WebSocket 信令客户端与服务端链路打通，支持自动从 TCP 回退到 WS。
- TUI 仪表盘支持多隧道列表、实时吞吐和链路统计展示。
- `TunnelRouter` 多目标路由能力，支持按 `@nodeId` 将映射分发到不同内网节点。
- 运行时带宽限速与 QoS 优先级接入。
- In-process signaling 集成测试基建，以及 P2P/KCP、WebSocket、限速等验证用例。

## Improved

- `SignalingService` 连接抽象统一为 `ISignalingConnection`，TCP/WS 共享一套消息处理逻辑。
- `ConnectionNegotiator` 数据面与 NAT / 打洞逻辑进一步解耦，便于维护和扩展。
- 配置对象、连接循环和 CLI 入口现在都会校验 `--signaling-transport` 与 `--ws-port`。
- README 与 `examples/*.json` 已同步到当前真实能力，避免文档与实现脱节。

## Fixed

- 修复 WebSocket 客户端和服务端在释放阶段可能阻塞，导致测试超时的问题。
- 修复控制帧误受限速/队列影响的问题。
- 修复隧道 TCP 读取过大时可能超过 64KB 接收缓冲限制的问题。

## Validation

- `dotnet test LanBridge.slnx -v minimal`
- 当前发布标签：`v0.2.0`
- 对应提交：`bfee994`

