# Implementation Plan: 集成测试框架

## Ordered Checklist

### Step 1: 产品代码最小改动（前置，为测试铺路）
- [ ] `SignalingService.cs`：加 `public int ActualPort => (_listener.Server.LocalEndpoint as IPEndPoint)?.Port ?? 0;`（暴露实际端口；`_listener` 在构造后即有 LocalEndpoint）。验证向后兼容。
- [ ] `WebSocketSignalingService.cs`：构造加 `bool bindAllNics = true` 参数；前缀用 `bindAllNics ? "http://+:" : "http://localhost:"`。`Program.cs` 调用处不传参（走默认 true，生产行为不变）。测试传 false。
- [ ] `dotnet build -c Release` 0 警告；`dotnet test` 现有 56 个仍全绿（无回归）。

**Review gate 1**：产品改动是纯加法，默认行为不变。自检通过即继续。

### Step 2: SignalingTestCluster + TestClient 辅助
- [ ] 新增 `src/LanBridge.Tests/Integration/SignalingTestCluster.cs`：`IAsyncLifetime`，进程内起 `SignalingService`（TCP port 0→ActualPort）+ 可选 `WebSocketSignalingService`（bindAllNics:false，临时端口）。`DisposeAsync` 停服务。
- [ ] 新增 `src/LanBridge.Tests/Integration/TestClient.cs`：包装 `SignalingClient`/`WebSocketSignalingClient`，`OnMessageReceived` 收集到 `Received` 列表 + `TaskCompletionSource`；`WaitForAsync<T>(timeout)` 带超时。
- [ ] 临时端口分配器：`TcpListener(IPAddress.Loopback, 0)` → 取端口 → 关闭 → 返回（给 WS 用）。竞态可接受。
- [ ] 编译通过（测试项目）。

### Step 3: TCP 信令往返（REQ-2, 5）
- [ ] `TcpSignalingTests.cs`：`Register` → `RegisterAck(Success)` 往返。`[Fact(Timeout=30000)]`。
- [ ] 共存测试（REQ-5）：TCP + WS 客户端同注册，各收各的 ack，不串扰。

### Step 4: WS 信令往返 + auto 回退（REQ-3, 4）
- [ ] `WsSignalingTests.cs`：WS `Register`→`RegisterAck`。
- [ ] `auto` 回退：只起 WS，TCP 不监听，`transportType=auto`，5s 内回退到 WS 收到 ack。
- [ ] 若 CI 上 `HttpListener` 失败，加 `OperatingSystem` 条件 skip 并注释原因（优先尝试不 skip）。

### Step 5: 连接协商 + 中继往返（REQ-6, 7）
- [ ] `NegotiationFlowTests.cs`：两客户端注册后，Extranet 发 `ConnectRequest` → 断言 Intranet 收 `HolePunchStart`、Extranet 收 `ConnectReady`。
- [ ] Extranet 发 `RelayRequest` → 双方收 `RelayAccept`（role 正确）。

### Step 6: 限速生效（REQ-8）
- [ ] `RateLimitIntegrationTests.cs`：`PeerTransportSession` + `SetRateLimit(new TokenBucket(100_000))`，发 1MB，断言耗时 ∈ [8s, 15s]（~10s ±容差）。桶检查在 SendCoreAsync 之前，即使 SendCoreAsync 空转（无 KCP）桶仍消耗——可测。若不可行（SendCoreAsync 抛异常），用 relay session 或最小 mock SendCoreAsync。

### Step 7: 全量验证
- [ ] `dotnet build LanBridge.slnx -c Release` 0 警告。
- [ ] `dotnet test LanBridge.slnx -c Release` 全绿（现有 56 + 新增）。
- [ ] 确认新测试在并行下不挂死、不端口冲突。
- [ ] 确认 WS 测试不需要管理员权限（localhost 前缀）。

## Validation Commands
```bash
dotnet build LanBridge.slnx -c Release
dotnet test LanBridge.slnx -c Release
```

## Review Gates
- Gate 1（Step 1 后）：产品改动纯加法，56 单测无回归。
- Gate 2（Step 7）：新测试全绿，无挂死，CI 可行。

## Rollback Points
- Step 1 后若发现产品改动有风险：回退 2 个改动，测试用固定端口 + `http://localhost` 临时方案。
- Step 6 若限速难直接测：降级为只断言 `SetRateLimit` 后 `_tokenBucket != null`（接线检查而非行为检查），仍能抓"从不调用"bug。

## Notes
- 不起子进程；直接实例化产品组件。
- 不测真实 UDP 打洞（Out of Scope）。
- 测试文件放 `src/LanBridge.Tests/Integration/` 子目录。
