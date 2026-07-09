# Implementation Plan: 数据面抽取

## Ordered Checklist

### Step 1: 新建 PeerSessionManager
- [ ] `src/LanBridge.Common/Network/PeerSessionManager.cs`：sealed class, IDisposable。
- [ ] 从 `ConnectionNegotiator` 原样搬入：`_sessions` 字典、`_activeSessionId`、`GetSession` 工厂（含 SetPriority/SetRateLimit/事件接线）、`SendAsync`×2、`SendHighPriorityAsync`×2、`Mode`/`IsConnected`、`GetStatsSnapshot`、`SetActiveSession`/`ActiveSessionId`。
- [ ] 事件 `OnDataReceived`/`OnSessionDataReceived` 在工厂里转发到本类事件。
- [ ] `Dispose`：dispose 所有 session。
- [ ] 编译通过。

### Step 2: ConnectionNegotiator 改委托
- [ ] 字段 `ConcurrentDictionary<string, PeerTransportSession> _sessions` → `PeerSessionManager _sessions`（两个 ctor 各自 `new PeerSessionManager(options)`）。
- [ ] 委托成员：`Mode`/`IsConnected`/`SendAsync`×2/`SendHighPriorityAsync`×2/`GetStatsSnapshot` → `_sessions.*`。
- [ ] ctor 接线事件转发：`_sessions.OnDataReceived += ...`、`_sessions.OnSessionDataReceived += ...`。
- [ ] control plane handler 的 `_activeSessionId = x` → `_sessions.SetActiveSession(x)`（`HandleConnectReadyAsync:454`、`HandleRelayAcceptAsync:499`；`HandleHolePunchStartAsync:478` 是新建 sessionId 不设 active——核对原逻辑保留）。
- [ ] `BeginHolePunchFromSignalAsync` 内 `GetSession(s).UseP2p` → `_sessions.GetSession(s).UseP2p`。
- [ ] `HandleRelayAcceptAsync:514` `GetSession(sessionId).UseRelay` → `_sessions.GetSession(sessionId).UseRelay`。
- [ ] `SendUnreliableAsync`：`GetSession(sessionId)` → `_sessions.GetSession(sessionId)`。
- [ ] 其他 `GetSession(_activeSessionId)` 引用 → 走 `Mode`/`IsConnected` 委托或 `_sessions.GetSession(_sessions.ActiveSessionId)`。
- [ ] `Dispose`：加 `_sessions.Dispose()`。

### Step 3: 验证
- [ ] `dotnet build LanBridge.slnx -c Release` 0 警告。
- [ ] `dotnet test LanBridge.slnx -c Release` 全绿（64 测试，含集成测试）——两轮确认无回归。
- [ ] 确认 `ConnectionNegotiator.cs` 行数显著下降（数据面 ~80 行移出）。
- [ ] 确认调用方文件零改动（git diff 只含 `ConnectionNegotiator.cs` + 新 `PeerSessionManager.cs`）。

## Validation Commands
```bash
dotnet build LanBridge.slnx -c Release
dotnet test LanBridge.slnx -c Release
```

## Review Gates
- Gate 1（Step 1 后）：`PeerSessionManager` 独立编译通过。
- Gate 2（Step 3）：64 测试全绿，调用方零改动。

## Rollback Points
- Step 2 后若事件转发签名不对导致行为异常：回退到内联（`PeerSessionManager` 删除，恢复原字段）。
- 集成测试若失败：先定位是事件转发问题还是工厂搬迁问题——两者都是机械性，易修。

## Notes
- 纯重构，无行为变化。
- `SendUnreliableAsync` 留在 negotiator（依赖 hole puncher，后续阶段）。
- 不动 `ISignalingHandler`、NAT/打洞、LAN 发现。
