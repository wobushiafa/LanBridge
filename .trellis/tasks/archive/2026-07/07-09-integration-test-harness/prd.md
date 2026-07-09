# 集成测试：信令与数据面端到端接线验证

## Goal

为 LanBridge 建立进程内集成测试框架，覆盖信令注册/连接协商/中继的端到端接线（TCP + WebSocket），以及带宽限速的实际生效。目标是防住"核心类已实现但运行时从不调用"这一整类 bug——本仓库的 WebSocket 信令和带宽 QoS 两个特性都曾处于该状态整整一个会话无人发现，因为现有测试全是纯单元测试，不验证接线。

## Background

### 现状
`src/LanBridge.Tests/` 现有 8 个测试类（`TokenBucketTests`/`PriorityFrameQueueTests`/`MessageSerializerTests`/`ProtocolTests`/`TunnelFrameTests`/`P2PConvTests`/`ConfigurationTests`/`BinaryHelperTests`），全部是纯单元测试，验证单个组件在隔离状态下的行为。**没有任何测试**验证：
- 客户端发的消息能到达服务端
- 服务端的响应（`RegisterAck`/`ConnectReady`/`RelayAccept`/`HolePunchStart`）能回到客户端
- 配置项（`--signaling-transport`、`RateLimitBytesPerSec`）真的影响运行时行为
- WS 客户端和 TCP 客户端能在同一服务器共存

### 历史教训
- WebSocket 信令：核心类（`SignalingTransportBase`/`WebSocketSignalingClient`/`WebSocketSignalingService`）已实现提交，但 `SetRateLimit`/`ProcessMessageFromTransportAsync` 的出站桥接从没接线——WS 客户端收不到服务端响应。单测全绿，功能完全不通。
- 带宽 QoS：`TokenBucket`/`PriorityFrameQueue`/`PeerTransportSession.SetRateLimit` 已实现且有单测，但运行时从不调用 `SetRateLimit`——限速形同虚设。单测全绿。
- 两者都是靠人工 trace 代码才在后续会话发现的。集成测试能在引入时立刻抓住。

## Requirements

### REQ-1: 进程内测试集群
新增 `SignalingTestCluster`（或同等测试辅助），在测试进程内启动 `SignalingService`（+ 可选 `WebSocketSignalingService`），使用 OS 分配的临时端口（port 0），`IDisposable`/`IAsyncLifetime` 管理生命周期。不起子进程。

### REQ-2: 信令注册往返（TCP）
TCP 客户端连接 → 发 `Register` → 收到 `RegisterAck(Success=true)`。断言响应真的到达客户端（捕获"出站路由断裂"类 bug）。

### REQ-3: 信令注册往返（WebSocket）
WS 客户端连接 → 发 `Register` → 收到 `RegisterAck`。这是曾经断过的路径（出站桥接），必须有测试。

### REQ-4: `auto` 回退
关闭/不启动 TCP 信令端口，客户端 `transportType=auto` 应回退到 WS 并完成注册。带超时，避免挂死。

### REQ-5: TCP + WS 共存
同一 `SignalingService`，一个 TCP 客户端和一个 WS 客户端同时注册；服务端按 clientId 路由响应互不串扰。

### REQ-6: 连接协商信令往返
Extranet 发 `ConnectRequest` → 服务端向 Intranet 发 `HolePunchStart` + 向 Extranet 发 `ConnectReady`。验证服务端的中介路由（不实际打 UDP 洞——见 Out of Scope）。

### REQ-7: 中继信令往返
Extranet 发 `RelayRequest` → 双方收到 `RelayAccept`。

### REQ-8: 限速实际生效
配置 `RateLimitBytesPerSec = R`，通过 `PeerTransportSession` 发送 N 字节（N >> R），断言耗时 ≥ N/R 秒（允许 ±20% 容差）。捕获"SetRateLimit 从不调用"类 bug。

### REQ-9: 超时与隔离
每个测试有整体超时（xunit `Timeout` 或 `CancellationTokenSource`），防止接线 bug 导致挂死。测试间端口隔离（临时端口），支持并行。

## Acceptance Criteria

- [ ] `SignalingTestCluster` 能在进程内启动 TCP + WS 信令服务并清理
- [ ] TCP 注册往返测试通过（客户端收到 `RegisterAck`）
- [ ] WS 注册往返测试通过
- [ ] `auto` 回退测试通过（TCP 不通→WS）
- [ ] TCP+WS 共存测试通过
- [ ] `ConnectRequest`→`HolePunchStart`+`ConnectReady` 往返测试通过
- [ ] `RelayRequest`→`RelayAccept` 往返测试通过
- [ ] 限速生效测试通过（耗时在容差内）
- [ ] 所有测试有超时保护，无挂死风险
- [ ] `dotnet test` 全绿（含新测试），`dotnet build -c Release` 0 警告
- [ ] 测试可在 CI 运行（Linux + Windows），无管理员权限依赖（或明确 skip WS on CI）

## Out of Scope

- **真实 UDP 打洞**：需要双 UDP socket + NAT 模拟，在进程内 loopback 难以真实复现；只测信令协商部分，不测 P2P 数据通路建立后的实际 UDP 传输。后续可加。
- **KCP 数据传输**：打洞成功后的 KCP 数据流不在本次范围（信令往返 + 限速即可覆盖接线）。
- **真实子进程启动 peer 二进制**：进程内实例化组件足够验证接线，子进程测试另开。
- **TUI 渲染**：不测 ANSI 输出。
- **跨网络/NAT 的端到端**：仅 loopback。

## Resolved Decisions

_(待 design.md 确定)_

## Open Questions

- WS `HttpListener` 在 CI（无管理员权限）能否绑定 `http://localhost:<ephemeral>/`？若不能，WS 测试在 CI skip 还是改用其他 WS 后端？
- 临时端口分配：`TcpListener(port=0)` 取端口后立即关再给 `HttpListener` 用，竞态可接受吗？
