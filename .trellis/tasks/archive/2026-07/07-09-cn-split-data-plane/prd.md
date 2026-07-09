# ConnectionNegotiator 拆分阶段 1：抽取数据面

## Goal

将 `ConnectionNegotiator`（872 行上帝类）的**数据面**——会话存储与管理、可靠/高优先 send、会话状态与统计——抽取到新类 `PeerSessionManager`。控制面（信令协商、NAT/打洞、LAN 发现）保留在 `ConnectionNegotiator` 并委托数据面。`ConnectionNegotiator` 的公共 API 与行为完全不变（调用方零影响）。这是分阶段拆分的第一阶段。

## Background

`ConnectionNegotiator` 同时承担：数据面（`_sessions` 字典 + `GetSession` 工厂 + `SendAsync`/`SendHighPriorityAsync` + `Mode`/`IsConnected`/`GetStatsSnapshot`）、控制面（信令 handler `HandleConnectReady/HolePunchStart/RelayAccept` + `RequestConnectionAsync`）、NAT/打洞子系统（`_holePuncher`/`_natDetection`/`_pendingPunches`/NAT keepalive）、LAN 发现。每加一个发送变体或会话特性都要在这个巨型类里戳。分阶段抽取以降低单次风险。

## Scope (Phase 1 only)

**抽取到 `PeerSessionManager`**：
- `_sessions: ConcurrentDictionary<string, PeerTransportSession>`
- `_activeSessionId`（active session 概念属于数据面）
- `GetSession(sessionId)` 工厂（创建 PeerTransportSession，按 `_options` 调 `SetPriority`/`SetRateLimit`，接线 `OnDataReceived`/`OnSessionDataReceived` 事件）
- `SendAsync(sessionId, data, off, len)`、`SendAsync(data, off, len)`（active）、`SendHighPriorityAsync`×2
- `Mode`、`IsConnected`（读 active session）
- `GetStatsSnapshot()`（读 active session）
- 事件转发：`OnDataReceived`、`OnSessionDataReceived`（从 session 转发出来供 control plane 暴露）

**留在 `ConnectionNegotiator`（本阶段不动）**：
- `SendUnreliableAsync`×2 —— 混合体（session 桶 + hole puncher 原始 UDP），是与 NAT 子系统的接缝，留到 NAT 抽取阶段。仍通过 `_sessions.GetSession(sessionId).ApplyRateLimitAsync` / `.SendAsync` 调用数据面。
- 控制面全部 handler、`StartAsync`、NAT/打洞、LAN 发现、`RequestConnectionAsync`。

**`ConnectionNegotiator` 改为委托**：
- 持有 `_sessions: PeerSessionManager`
- `SendAsync`/`SendHighPriorityAsync`/`Mode`/`IsConnected`/`GetStatsSnapshot` → 委托 `_sessions`
- `OnDataReceived`/`OnSessionDataReceived` → 从 `_sessions` 转发
- 控制面 handler 通过 `_sessions.GetSession(sessionId).UseP2p/UseRelay` 挂载传输，`_sessions.SetActiveSession(sessionId)` 改 active

## Requirements

### REQ-1: 新类 PeerSessionManager
`src/LanBridge.Common/Network/PeerSessionManager.cs`，`sealed class ... IDisposable`。持有 `_sessions` + `_activeSessionId`，ctor 接受 `PeerConnectionOptions`（取 Verbose/RateLimitBytesPerSec/Priority 用于工厂）。

### REQ-2: 会话工厂
`GetSession(sessionId)` 创建 `PeerTransportSession`，调 `SetPriority`/`SetRateLimit`（按 options），接线 `OnDataReceived`/`OnSessionDataReceived` 事件转发。行为与现有 `ConnectionNegotiator.GetSession` 完全一致。

### REQ-3: send 委托
`SendAsync(sessionId,...)`/`SendAsync(...)`（active）/`SendHighPriorityAsync`×2 转发到对应 session。行为不变。

### REQ-4: 状态与统计
`Mode`/`IsConnected`（active session）、`GetStatsSnapshot()`（active session）。`SetActiveSession(sessionId)` 供 control plane 改 active。

### REQ-5: 传输挂载
暴露 `GetSession(sessionId)`（或 `AttachP2p(sessionId, KcpSession)`/`AttachRelay(sessionId, RelayClient)`）供 control plane handler 调 `UseP2p`/`UseRelay`。

### REQ-6: ConnectionNegotiator 委托 + 公共 API 不变
`ConnectionNegotiator` 的 public 成员签名/行为不变（`IntranetPeer`/`TunnelRouter`/`KcpTest`/`LanDiscoveryService`/`SharedUdpStack`/`SignalingMessageDispatcher` 零改动）。`ISignalingHandler` 仍实现在 `ConnectionNegotiator`。

### REQ-7: SendUnreliableAsync 保持
`SendUnreliableAsync`×2 留在 `ConnectionNegotiator`，通过 `_sessions.GetSession(sessionId)` 调桶/Send。行为不变。

## Acceptance Criteria

- [ ] `PeerSessionManager` 类创建，含 REQ-1~5 的全部成员
- [ ] `ConnectionNegotiator` 的 send/状态/统计成员改为委托 `_sessions`
- [ ] `ConnectionNegotiator` public API 签名零变化
- [ ] 所有调用方（IntranetPeer/TunnelRouter/KcpTest/LanDiscoveryService/SharedUdpStack）零改动
- [ ] `dotnet build -c Release` 0 警告
- [ ] `dotnet test` 全绿（现有 64 测试，含集成测试覆盖 send/会话路径）—— 无行为回归
- [ ] `ConnectionNegotiator.cs` 行数显著下降（数据面代码移出）

## Out of Scope (后续阶段)

- NAT/打洞子系统抽取（`_holePuncher`/`_natDetection`/`_pendingPunches`/NAT keepalive/`BeginHolePunchFromSignalAsync`）
- LAN 发现抽取
- `SendUnreliableAsync` 迁移（依赖 NAT 子系统）
- 控制面 handler 拆分
- `ISignalingHandler` 重新归属

## Resolved Decisions

- **D1: 分阶段而非大爆炸**——降低单次风险，每阶段独立可测（用户选定）。
- **D2: 公共 API 不变**——`ConnectionNegotiator` 作 facade 委托，调用方零影响。后续阶段再考虑是否拆 facade。
- **D3: `SendUnreliableAsync` 留控制面**——它是数据面桶与 NAT hole puncher 的混合体，迁移依赖 NAT 子系统先抽出。
- **D4: `PeerSessionManager` 放 `LanBridge.Common/Network/`**——与 `PeerTransportSession` 同目录同命名空间。
