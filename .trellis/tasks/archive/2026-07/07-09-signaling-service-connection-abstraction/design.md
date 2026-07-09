# Design: SignalingService 统一连接抽象

## Architecture

`SignalingService` 从"TCP 原始对象 + WS 委托 record 双轨"变为"单一 `ISignalingConnection` 注册表"。

```
SignalingService
  ├── _connections: ConcurrentDictionary<string, ISignalingConnection>  ← 单一
  ├── _nodes / _clientToNode (transport-agnostic, 不变)
  └── StartAsync → accept TCP → _connections[id] = new TcpSignalingConnection(client)
                   HandleClientAsync(id, conn) 读 conn.Stream
        SendToClientAsync(id, msg) → conn.SendAsync
        DisconnectClientAsync(id) → conn.DisconnectAsync + remove + node 清理
        RegisterConnection(id, conn) ← WS 路径：new BridgeSignalingConnection(...)
        UnregisterConnection(id) ← WS 断开清理
```

## Component Design

### 1. ISignalingConnection (`ISignalingConnection.cs`)

```csharp
public interface ISignalingConnection : IAsyncDisposable
{
    Task SendAsync(BaseMessage message, CancellationToken ct);
    Task DisconnectAsync();
}
```
（不加 `IsConnected`——当前 send/disconnect 不依赖它；`SendToClientAsync` try/catch 即可。若需再加。）

### 2. TcpSignalingConnection (`TcpSignalingConnection.cs`)

```csharp
public sealed class TcpSignalingConnection : ISignalingConnection, IDisposable
{
    private readonly TcpClient _tcpClient;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public TcpSignalingConnection(TcpClient client)
    {
        _tcpClient = client;
        _stream = client.GetStream();
    }

    public NetworkStream Stream => _stream;  // 供 HandleClientAsync 读循环

    public async Task SendAsync(BaseMessage message, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            var data = Encoding.UTF8.GetBytes(MessageSerializer.SerializeToString(message));
            var lengthBytes = BitConverter.GetBytes(data.Length);
            await _stream.WriteAsync(lengthBytes, 0, 4, ct);
            await _stream.WriteAsync(data, 0, data.Length, ct);
            await _stream.FlushAsync(ct);
        }
        finally { _sendLock.Release(); }
    }

    public async Task DisconnectAsync()
    {
        await _sendLock.WaitAsync();
        try { _stream.Dispose(); _tcpClient.Dispose(); }
        finally { _sendLock.Release(); _sendLock.Dispose(); }
    }
    // Dispose 同 DisconnectAsync（IAsyncDisposable/IDisposable）
}
```
（原 `SendToClientAsync` 的 TCP 分支 + `_sendLocks` 逻辑搬入。错误处理在 `SignalingService.SendToClientAsync` 的 try/catch 外层。）

### 3. BridgeSignalingConnection (`BridgeSignalingConnection.cs`)

```csharp
public sealed class BridgeSignalingConnection : ISignalingConnection
{
    private readonly Func<BaseMessage, CancellationToken, Task> _send;
    private readonly Func<Task> _disconnect;
    public BridgeSignalingConnection(Func<BaseMessage, CancellationToken, Task> send, Func<Task>? disconnect = null)
    { _send = send; _disconnect = disconnect ?? (() => Task.CompletedTask); }
    public Task SendAsync(BaseMessage m, CancellationToken ct) => _send(m, ct);
    public Task DisconnectAsync() => _disconnect();
}
```
（即原 `TransportBridge` record 的类化版本。）

### 4. SignalingService 改造

**删字段**：`_clients`/`_streams`/`_sendLocks`/`_transportBridges` + `TransportBridge` record + `RegisterTransportSender`/`UnregisterTransportSender`/`OnTransportClientDisconnected`（改名/合并）。

**加字段**：`_connections: ConcurrentDictionary<string, ISignalingConnection>`。

**TCP accept loop**（StartAsync 内 ~108-111）：
```csharp
var client = await _listener.AcceptTcpClientAsync();
var clientId = Guid.NewGuid().ToString("N")[..8];
var conn = new TcpSignalingConnection(client);
_connections[clientId] = conn;
_ = Task.Run(() => HandleClientAsync(clientId, conn));
```

**RegisterConnection**（替代 RegisterTransportSender）：
```csharp
public void RegisterConnection(string clientId, ISignalingConnection conn) => _connections[clientId] = conn;
```

**UnregisterConnection**（替代 OnTransportClientDisconnected 的清理部分）：
```csharp
public void UnregisterConnection(string clientId)
{
    _connections.TryRemove(clientId, out _);
    // node 清理（原 OnTransportClientDisconnected 的 _clientToNode/_nodes 部分）
    if (_clientToNode.TryRemove(clientId, out var nodeId)) { _nodes.TryRemove(nodeId, out _); ... }
}
```

**SendToClientAsync**（单路径）：
```csharp
public async Task SendToClientAsync(string clientId, BaseMessage message)
{
    if (!_connections.TryGetValue(clientId, out var conn)) return;
    try { await conn.SendAsync(message, CancellationToken.None); }
    catch (Exception ex) { ConsoleStatusWriter.WriteServerStatus("Signaling", $"Send error to {clientId}: {ex.Message}", Red); }
}
```

**DisconnectClientAsync**（去 TcpClient 参数）：
```csharp
public async Task DisconnectClientAsync(string clientId)
{
    if (_connections.TryRemove(clientId, out var conn))
    {
        try { await conn.DisconnectAsync(); } catch { }
    }
    // node 清理（同 UnregisterConnection 的 _clientToNode/_nodes 部分）—— 提取私有方法
    RemoveNodeBinding(clientId);
}
```
`HandleRegisterAsync` reject（~240）：`await DisconnectClientAsync(clientId, client)` → `await DisconnectClientAsync(clientId)`。

**HandleClientAsync** 读循环：`HandleClientAsync(string clientId, TcpSignalingConnection conn)` 或 `ISignalingConnection`+cast。**决策**：签名 `HandleClientAsync(string clientId, TcpSignalingConnection conn)`（TCP 专用读循环，类型确定），读 `conn.Stream`。`HandleClientAsync` 内的 `client.Dispose()`（finally ~203）→ 移除（conn 自己 dispose 了，`DisconnectClientAsync`/finally 调 `conn.DisconnectAsync`）。finally 清理：`_connections.TryRemove` + `RemoveNodeBinding(clientId)`（原三 dict remove 合一）。

**提取 `RemoveNodeBinding(string clientId)`**：`_clientToNode.TryRemove` + `_nodes.TryRemove` + telemetry + status，供 `UnregisterConnection`/`DisconnectClientAsync`/finally 共用。

### 5. WebSocketSignalingService 调用方更新
- `RegisterTransportSender(clientId, (msg,_) => SendToClientAsync(clientId, msg), () => ws.State==Open ? ws.CloseAsync(...) : Task.CompletedTask)` → `RegisterConnection(clientId, new BridgeSignalingConnection((msg,_) => SendToClientAsync(clientId, msg), () => ws.State==Open ? ws.CloseAsync(...) : Task.CompletedTask))`。
- `OnTransportClientDisconnected(clientId)`（finally）→ `UnregisterConnection(clientId)`。

## Compatibility

- `SignalingService` 对外 public：`SendToClientAsync`/`ProcessMessageFromTransportAsync`/`OnMessageReceived`/`ActualPort`/`StartAsync` 不变；`RegisterTransportSender`→`RegisterConnection`、`OnTransportClientDisconnected`→`UnregisterConnection`（仅 `WebSocketSignalingService` 调用，一并更新）；`DisconnectClientAsync` 去 `TcpClient?` 参数（仅 `HandleRegisterAsync` 内部调用）。
- 行为等价（纯重构）。
- 集成测试是安全网（TCP/WS 注册往返/共存/协商全路径）。

## Rollback

3 个新类是独立文件，删掉即回滚。`SignalingService`/`WebSocketSignalingService` 改动内联回退。

## Native AOT

接口 + sealed 类，无反射。接口虚调用在信令控制路径（非数据面热路径），开销可忽略。

## Risk

- **中低**：纯重构 + 集成测试覆盖 TCP/WS 全路径。
- 主要风险：TCP 读循环 `HandleClientAsync` 的 stream 来源切换 + finally 清理遗漏。缓解：`NegotiationFlowTests`（TCP↔WS 跨传输协商）+ `WsSignalingTests` 覆盖。
- `DisconnectClientAsync` 签名变化只影响 `HandleRegisterAsync`（内部）——无外部调用方。
