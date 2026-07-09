# SignalingService 统一连接抽象

## Goal

清理 `SignalingService` 的双轨连接管理：TCP 用 3 个 dict（`_clients`/`_streams`/`_sendLocks`）、WS 用 `_transportBridges`，`SendToClientAsync` 桥优先再 fallback TCP，`DisconnectClientAsync(clientId, TcpClient?)` 按传输类型分支。统一为单一 `ISignalingConnection` 抽象 + 单一注册表 + 单条 send/disconnect 路径。加第 3 种传输时不再需要再加一条分支。

## Background

WS 信令是后接的补丁：`SignalingService` 原本只认 TCP（`TcpClient`/`NetworkStream`/`SemaphoreSlim`）。加 WS 时为不动 TCP 路径，引入了并行的 `_transportBridges`（`TransportBridge` record: `SendAsync`+`DisconnectAsync` 委托）。结果：两个连接表示（TCP 原始对象 vs WS 委托 record）、两套注册（accept loop 填 3 dict vs `RegisterTransportSender` 填 bridge）、两套 send（bridge-first / TCP length-prefix）、`DisconnectClientAsync` 带 `TcpClient?` 可空参数按传输分支。能用但补丁味重，加 QUIC/第 3 传输会再膨胀。

## Scope

### REQ-1: ISignalingConnection 接口
`src/LanBridge.SignalingServer/ISignalingConnection.cs`：`SendAsync(BaseMessage, CancellationToken)` + `DisconnectAsync()`（+ 可选 `IsConnected`）。

### REQ-2: TcpSignalingConnection 包装类
`src/LanBridge.SignalingServer/TcpSignalingConnection.cs`：`sealed ... ISignalingConnection, IDisposable`。持 `NetworkStream _stream` + `SemaphoreSlim _sendLock` + `TcpClient _tcpClient`。`SendAsync` 做 4 字节长度前缀 + JSON 写（带 lock，原 `SendToClientAsync` 的 TCP 分支逻辑搬入）。`DisconnectAsync` dispose 三个。暴露 `Stream`（供 `HandleClientAsync` 读循环用）。

### REQ-3: BridgeSignalingConnection 包装类
`src/LanBridge.SignalingServer/BridgeSignalingConnection.cs`：`sealed ... ISignalingConnection`。持 `Func<BaseMessage,CT,Task> _send` + `Func<Task> _disconnect`（即原 `TransportBridge`）。`SendAsync`/`DisconnectAsync` 委托。WS 路径用。

### REQ-4: SignalingService 单一注册表 + 单路径
- 删 `_clients`/`_streams`/`_sendLocks`/`_transportBridges` 4 个 dict，替为 `ConcurrentDictionary<string, ISignalingConnection> _connections`。
- TCP accept loop：`_connections[clientId] = new TcpSignalingConnection(client)`。
- `RegisterTransportSender(clientId, sender, disconnectAsync)` → 改名 `RegisterConnection(clientId, ISignalingConnection)`（或保留旧名内部包 BridgeSignalingConnection）。**决策**：改名 `RegisterConnection`，WS 调用方（`WebSocketSignalingService`）改传 `new BridgeSignalingConnection(sender, disconnect)`。
- `SendToClientAsync(clientId, msg)` → `_connections.TryGetValue` → `await conn.SendAsync(msg, ct)`。单路径，无传输分支。
- `DisconnectClientAsync(clientId, TcpClient?)` → `DisconnectClientAsync(clientId)`（去掉 TcpClient 参数）→ `_connections.TryGetValue` → `await conn.DisconnectAsync()` + remove + node 清理。`HandleRegisterAsync` reject 调用更新。
- `OnTransportClientDisconnected(clientId)` → 合并到 `UnregisterConnection(clientId)`（或保留名，内部 = remove + 清理）。WS 调用方更新。
- `HandleClientAsync` 读循环：从 `TcpSignalingConnection.Stream` 读 4 字节长度 + JSON（原逻辑不变，只是 stream 来源换了）。
- finally 块清理：移除 `_connections[clientId]` + node 表（原 `_clients`/`_streams`/`_sendLocks` 三 remove 合一）。

### REQ-5: WebSocketSignalingService 调用方更新
- `RegisterTransportSender(clientId, sender, disconnect)` → `RegisterConnection(clientId, new BridgeSignalingConnection(sender, disconnect))`。
- `OnTransportClientDisconnected(clientId)` → `UnregisterConnection(clientId)`（或保留名）。

### REQ-6: 公共 API 收敛 + 行为不变
- `ProcessMessageFromTransportAsync`（WS 入站）不变。
- `OnMessageReceived` 事件不变。
- `ActualPort` 不变。
- `SendToClientAsync`/`DisconnectClientAsync` 内部简化但对外行为等价。
- 集成测试（TCP/WS 注册往返、共存、`NegotiationFlowTests`）全绿，无回归。

## Acceptance Criteria

- [ ] `ISignalingConnection` + `TcpSignalingConnection` + `BridgeSignalingConnection` 三个类型创建
- [ ] `SignalingService` 用单一 `_connections` 注册表，无 `_clients`/`_streams`/`_sendLocks`/`_transportBridges`
- [ ] `SendToClientAsync` 单路径（无传输分支）
- [ ] `DisconnectClientAsync` 去掉 `TcpClient?` 参数
- [ ] `WebSocketSignalingService` 调用方更新到新 API
- [ ] `dotnet build -c Release` 0 警告
- [ ] `dotnet test` 全绿（69 测试，含集成测试覆盖 TCP/WS 注册往返/共存/协商）—— 无回归
- [ ] Native AOT 兼容（接口虚调用在非热路径服务端代码可接受；无反射）

## Out of Scope

- 客户端侧 `SignalingTransportBase`（已是统一抽象，不动）
- `SignalingService` 的消息处理逻辑（`ProcessMessageAsync`/各 handler）——只动连接管理
- 节点注册/token 校验逻辑——不动
- 第 3 种传输的实际添加（本任务只做准备，让加第 3 传输时不再加分支）

## Resolved Decisions

- **D1: 接口而非抽象基类**——服务端连接无共享状态（TCP 持 stream/lock，WS 持委托），接口足够。非热路径（信令控制消息，非数据面），接口虚调用开销可忽略。与客户端 `SignalingTransportBase`（热路径、需共享锁逻辑）不同。
- **D2: 改名 `RegisterTransportSender`→`RegisterConnection`**——统一命名，调用方（仅 `WebSocketSignalingService`）一并更新。
- **D3: `HandleClientAsync` 读循环留 `SignalingService`**——它调 `ProcessMessageAsync`（服务端逻辑），不搬进 connection。connection 只暴露 `Stream` 供读。
- **D4: 三个类放 `LanBridge.SignalingServer` 命名空间**——与 `SignalingService` 同。

## Open Questions

- `ISignalingConnection` 放独立文件还是合进 `SignalingService.cs`？倾向独立文件（清晰）。
