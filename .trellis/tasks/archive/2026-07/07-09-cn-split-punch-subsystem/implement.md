# Implementation Plan: NAT/打洞子系统抽取

## Ordered Checklist

### Step 1: 新建 PeerPunchCoordinator
- [ ] `src/LanBridge.Common/Network/PeerPunchCoordinator.cs`：sealed class, IDisposable。
- [ ] 搬入状态：`_holePuncher`/`_pendingPunches`/`_isHolePunching`/`_relayProbeCts`/`_publicEndPoint`/`_publicEndPointV6`/`_natDetection`/`_natDiagnostics`。
- [ ] 事件 OUT：`OnStatusChanged`/`OnModeChanged`/`OnP2pUnhealthy`/`OnUnreliableDataReceived`。
- [ ] 回调 IN（ctor 注入）：`attachP2p`/`isConnected`/`activeSessionIdProvider`/`isSignalingConnected`/`requestRelayAsync`/`requestConnectionAsync`/`registerNodeAsync`/`modeProvider`。
- [ ] 搬入方法：`ConfigureHolePuncherEvents`（替换字段访问为回调/事件）、`BeginHolePunchFromSignalAsync`、`StartNatKeepAliveLoop`、`StartRelayProbeLoop`、`DetectNatAsync`、`EnsureConnectedAsync`、`SendUnreliableAsync`×2、`HandleLanDiscoveryRequestAsync`、`ExplainP2pFailure`、`GetHolePuncher`、`StartAsync(SharedUdpStack?)`、`Dispose`。
- [ ] 编译通过。

### Step 2: ConnectionNegotiator 改编排
- [ ] 删 NAT/打洞字段，加 `_punch: PeerPunchCoordinator`。
- [ ] ctor 注入回调（`attachP2p: (id,s) => _sessions.GetSession(id).UseP2p(s)` 等 8 个）。
- [ ] `StartAsync`：删 NAT/打洞编排，改 `_punch.StartAsync(_injectedUdpStack)` + 接事件转发 + 原有信令触发。
- [ ] 委托成员：`PublicEndPoint`/`PublicEndPointV6`/`SendUnreliableAsync`×2/`HandleLanDiscoveryRequestAsync`/`EnsureConnectedAsync` → `_punch.*`。
- [ ] handler：`HandleConnectReady/HolePunchStart` 调 `_punch.BeginHolePunchFromSignalAsync`；`HandleRelayAccept` 调 `_punch.StartRelayProbeLoop()`。
- [ ] `_lanDiscovery` 留 negotiator（避免循环依赖）；`StartLanDiscoveryBroadcast` 用 `_punch.GetHolePuncher()`。
- [ ] `Dispose`：加 `_punch.Dispose()`。
- [ ] 删死代码（搬走的方法体）。

### Step 3: 验证
- [ ] `dotnet build LanBridge.slnx -c Release` 0 警告。
- [ ] `dotnet test` 全绿（64 测试，两轮）—— 重点看 `NegotiationFlowTests`（ConnectRequest/HolePunchStart/RelayAccept）、`WsSignalingTests.Auto_FallsBackToWs`、`RateLimitIntegrationTests`。
- [ ] `git diff --stat` 只含 `ConnectionNegotiator.cs` + 新 `PeerPunchCoordinator.cs`（调用方零改动）。
- [ ] `ConnectionNegotiator.cs` 行数显著下降。

## Validation Commands
```bash
dotnet build LanBridge.slnx -c Release
dotnet test LanBridge.slnx -c Release
```

## Review Gates
- Gate 1（Step 1 后）：`PeerPunchCoordinator` 独立编译。
- Gate 2（Step 3）：64 测试全绿，调用方零改动。

## Rollback Points
- Step 2 后若行为漂移（relay 不触发/keepalive 不重注册）：定位是哪个回调/事件接线遗漏，补上即可（机械性）。
- 若循环依赖处理不当：回退 `_lanDiscovery` 到纯 negotiator 内联。

## Notes
- 纯重构，零行为变化。
- LAN 发现编排（broadcast/handle request）留 negotiator 规避 `LanDiscoveryService` 循环依赖。
- `OnModeChanged` 在 handler 里仍由 negotiator 直接 raise（relay 模式由控制面决定）。
