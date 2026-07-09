# Implementation Plan: SignalingService 统一连接抽象

## Ordered Checklist

### Step 1: 新建三个类型
- [ ] `ISignalingConnection.cs`：接口（`SendAsync`+`DisconnectAsync`+`IAsyncDisposable`）。
- [ ] `TcpSignalingConnection.cs`：sealed，持 `TcpClient`/`NetworkStream`/`SemaphoreSlim`，`SendAsync`（4 字节长度前缀+JSON，带锁，原 `SendToClientAsync` TCP 分支逻辑），`DisconnectAsync`（dispose 三者），`Stream` 属性供读循环。
- [ ] `BridgeSignalingConnection.cs`：sealed，持 send/disconnect 委托，委托 `SendAsync`/`DisconnectAsync`。
- [ ] 编译通过（独立类）。

### Step 2: SignalingService 改造
- [ ] 删字段 `_clients`/`_streams`/`_sendLocks`/`_transportBridges` + `TransportBridge` record；加 `_connections`。
- [ ] 删/改名 `RegisterTransportSender`→`RegisterConnection(clientId, ISignalingConnection)`；`UnregisterTransportSender`+`OnTransportClientDisconnected`→`UnregisterConnection(clientId)`（含 node 清理）。
- [ ] 提取 `RemoveNodeBinding(clientId)` 私有方法（`_clientToNode`/`_nodes`/telemetry/status），供 `UnregisterConnection`/`DisconnectClientAsync`/finally 共用。
- [ ] TCP accept loop：`_connections[clientId] = new TcpSignalingConnection(client)`，`HandleClientAsync(clientId, conn)`。
- [ ] `SendToClientAsync`：单路径 `_connections.TryGetValue` → `conn.SendAsync`。
- [ ] `DisconnectClientAsync(clientId, TcpClient?)` → `DisconnectClientAsync(clientId)`：`_connections.TryRemove` → `conn.DisconnectAsync` + `RemoveNodeBinding`。`HandleRegisterAsync` reject 调用更新。
- [ ] `HandleClientAsync(string clientId, TcpSignalingConnection conn)`：读 `conn.Stream`（4 字节长度+JSON，原逻辑）；finally 改 `_connections.TryRemove` + `RemoveNodeBinding`（删原三 dict remove + `client.Dispose()`——conn 自己 dispose）。

### Step 3: WebSocketSignalingService 调用方更新
- [ ] `RegisterTransportSender(...)` → `RegisterConnection(clientId, new BridgeSignalingConnection(...))`。
- [ ] `OnTransportClientDisconnected(clientId)` → `UnregisterConnection(clientId)`。

### Step 4: 验证
- [ ] `dotnet build LanBridge.slnx -c Release` 0 警告。
- [ ] `dotnet test` 全绿（69 测试，两轮）——重点 `NegotiationFlowTests`（TCP↔WS 协商）、`WsSignalingTests`（WS 注册/auto）、`TcpSignalingTests`（TCP 注册/共存）。
- [ ] `git diff --stat`：`SignalingService.cs`+`WebSocketSignalingService.cs` 改 + 3 新文件；无其他调用方。

## Validation Commands
```bash
dotnet build LanBridge.slnx -c Release
dotnet test LanBridge.slnx -c Release
```

## Review Gates
- Gate 1（Step 1 后）：三个类型独立编译。
- Gate 2（Step 4）：69 测试全绿。

## Rollback Points
- Step 2 后若 TCP 读循环/finally 清理有遗漏：集成测试会抓（NegotiationFlow 跨传输）。
- 若 `DisconnectAsync` 的 dispose 顺序导致问题（lock 先 dispose 再 stream）：调整顺序。

## Notes
- 纯重构，零行为变化。
- `HandleClientAsync` 留 SignalingService（调 ProcessMessageAsync）。
- `DisconnectClientAsync` 签名变化仅影响内部 HandleRegisterAsync。
