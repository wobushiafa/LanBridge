# ConnectionNegotiator 拆分阶段 2：抽取 NAT/打洞子系统

## Goal

将 `ConnectionNegotiator` 的 **NAT/打洞子系统**——hole puncher 编排、pending punch 追踪、NAT 探测与 keep-alive、relay probe、LAN 发现广播——抽取到新类 `PeerPunchCoordinator`。这是 Phase 1（数据面）之后的第二阶段。子系统通过**事件 + 少量回调委托**与数据面（`PeerSessionManager`）和控制面（`ConnectionNegotiator`）解耦，`ConnectionNegotiator` 公共 API 与行为不变。

## Background

Phase 1 抽走了数据面（`PeerSessionManager`）。但 NAT/打洞子系统仍留在 `ConnectionNegotiator`，且是**中心耦合点**：它同时触达数据面（`_sessions.GetSession().UseP2p`、`ActiveSessionId`、`OnDataReceived`/`OnSessionDataReceived`、`IsConnected`/`Mode`）、控制面（`OnModeChanged`、`OnStatusChanged`、`RequestConnectionAsync`、`RegisterNodeAsync`、`IsSignalingConnected`）和信令（`_signalingConnectionLoop.Transport` 发 `RelayRequest`）。

子系统成员：`_holePuncher`、`_pendingPunches`、`_isHolePunching`、`_relayProbeCts`、`_publicEndPoint`/`_publicEndPointV6`、`_natDetection`、`_natDiagnostics`、`_lanDiscovery`；方法 `ConfigureHolePuncherEvents`、`BeginHolePunchFromSignalAsync`、`StartNatKeepAliveLoop`、`StartRelayProbeLoop`、`StartLanDiscoveryBroadcast`、`HandleLanDiscoveryRequestAsync`、`DetectNatAsync`、`GetHolePuncher`、`ExplainP2pFailure`、`HandleP2pUnhealthyAsync`、`RequestRelayAsync`/`RequestRelayIfAllowedAsync`、`ProcessErrorAsync`。

## Scope (Phase 2)

**抽取到 `PeerPunchCoordinator`**：
- 状态：`_holePuncher`、`_pendingPunches`、`_isHolePunching`、`_relayProbeCts`、`_publicEndPoint`/`_publicEndPointV6`、`_natDetection`、`_natDiagnostics`、`_lanDiscovery`
- hole puncher 生命周期与事件接线（`ConfigureHolePuncherEvents`）
- `BeginHolePunchFromSignalAsync`、`StartNatKeepAliveLoop`、`StartRelayProbeLoop`、`StartLanDiscoveryBroadcast`、`HandleLanDiscoveryRequestAsync`、`DetectNatAsync`、`ExplainP2pFailure`
- `PublicEndPoint`/`PublicEndPointV6` 属性

**留在 `ConnectionNegotiator`**（控制面）：
- 信令 handler（`HandleConnectReady/HolePunchStart/RelayAccept/RegisterAck/Error`）——它们调用 coordinator 的 `BeginHolePunchFromSignalAsync` 等
- `RequestConnectionAsync`、`RegisterNodeAsync`、`RequestRelayAsync`（发信令消息，属控制面；coordinator 通过回调委托调用）
- `StartAsync`——编排（建 coordinator、调其 Start、触发注册/连接请求）
- `SendUnreliableAsync`——**迁移到 coordinator**（它用 `_holePuncher`；Phase 1 留在 negotiator 就是因为依赖本子系统）

## Requirements

### REQ-1: 新类 PeerPunchCoordinator
`src/LanBridge.Common/Network/PeerPunchCoordinator.cs`，`sealed class ... IDisposable`。持有上述全部 NAT/打洞状态。

### REQ-2: 事件出射（向控制面/数据面通知）
coordinator 暴露事件：`OnStatusChanged`、`OnModeChanged`、`OnP2pUnhealthy`、`OnUnreliableDataReceived`（转给数据面 OnDataReceived/OnSessionDataReceived）。`ConnectionNegotiator` 在 `StartAsync` 接线转发。

### REQ-3: 回调入射（coordinator 需要的控制面/数据面能力）
coordinator ctor 或 Start 接受委托：
- `AttachP2p(string sessionId, KcpSession session)` → 调 `_sessions.GetSession(sessionId).UseP2p`（数据面）
- `Func<bool> IsConnected` / `Func<string> ActiveSessionIdProvider` / `Func<bool> IsSignalingConnected`（状态查询）
- `RequestRelayAsync` / `RequestConnectionAsync(bool force)` / `RegisterNodeAsync`（控制面动作，coordinator 在 relay fallback / keepalive / probe 时回调）

### REQ-4: SendUnreliableAsync 迁移
`SendUnreliableAsync`×2 移到 coordinator（它持有 `_holePuncher` + 通过 `AttachP2p`/`GetSession` 拿 session 桶）。`ConnectionNegotiator` 委托 `=> _punch.SendUnreliableAsync(...)`。

### REQ-5: hole puncher 编排完整搬迁
`ConfigureHolePuncherEvents` 的 4 个事件（OnHolePunched→建 KcpSession+AttachP2p+OnModeChanged；OnLanAdvertised→trigger；OnError→status；OnUnreliableDataReceived→OnUnreliableDataReceived 事件）原样搬到 coordinator，事件出射 + 回调入射替代直接访问 negotiator 字段。

### REQ-6: ConnectionNegotiator 委托 + 公共 API 不变
public API 签名零变化。`PublicEndPoint`/`PublicEndPointV6` 委托 coordinator。`StartAsync` 建 coordinator、接事件转发、接回调委托、调 `coordinator.StartAsync`。

### REQ-7: 调用方零改动
`IntranetPeer`/`TunnelRouter`/`KcpTest`/`LanDiscoveryService`/`SharedUdpStack`/`SignalingMessageDispatcher` 零改动。注意 `LanDiscoveryService` ctor 现按 `ConnectionNegotiator` 类型注入——保持（coordinator 不接手 LanDiscoveryService 的注入，LAN 发现仍由 negotiator 在 StartAsync 里用 coordinator 的 hole puncher 建）。

## Acceptance Criteria

- [ ] `PeerPunchCoordinator` 类创建，含 REQ-1~5 全部成员
- [ ] `ConnectionNegotiator` 的 NAT/打洞成员改为委托/编排 coordinator
- [ ] `ConnectionNegotiator` public API 签名零变化
- [ ] 调用方零改动
- [ ] `dotnet build -c Release` 0 警告
- [ ] `dotnet test` 全绿（64 测试，两轮，含集成测试覆盖 P2P/relay 路径）—— 无行为回归
- [ ] `ConnectionNegotiator.cs` 行数显著下降

## Out of Scope (后续阶段)

- LAN 发现的进一步独立抽取（`LanDiscoveryService` 本身已是独立类；本阶段只搬 `StartLanDiscoveryBroadcast`/`HandleLanDiscoveryRequestAsync` 的编排）
- 控制面 handler 拆分
- `ISignalingHandler` 重新归属
- `RequestConnectionAsync`/`RegisterNodeAsync`/`RequestRelayAsync` 搬出 negotiator（它们发信令，属控制面，留）

## Resolved Decisions

- **D1: 事件出射 + 回调入射解耦**——coordinator 不直接持 negotiator 引用（避免循环依赖），用事件通知 + 委托回调。
- **D2: `SendUnreliableAsync` 迁移到 coordinator**——它依赖 `_holePuncher`，Phase 1 留在 negotiator 正是为等本阶段。
- **D3: `RequestRelayAsync` 留控制面**——它发信令消息（`RelayRequest` via `_signalingConnectionLoop.Transport`），属控制面职责；coordinator 通过回调委托触发。
- **D4: 公共 API 不变**——facade 委托，调用方零影响。

## Open Questions

- 回调委托用 `Action`/`Func` 还是定义 `IPunchHost` 接口？倾向委托（更轻、AOT 友好、无接口虚调用顾虑），但若回调数 >5 可能接口更清晰。implement 时按实际数量定。
