# Design: 集成测试框架

## Architecture

进程内测试集群，直接实例化产品代码组件，loopback 通信。

```
SignalingTestCluster (test helper, IDisposable)
  ├── SignalingService (TCP, ephemeral port)
  ├── WebSocketSignalingService? (WS, ephemeral port, optional)
  ├── StunService? (optional, for negotiator path)
  └── RelayService? (optional, for relay path)

TestClient (helper)
  ├── SignalingTransportBase (TCP or WS, real client)
  └── 捕获 OnMessageReceived 的断言
```

测试直接调用产品代码的 public API（`SignalingService`、`SignalingClient`、`WebSocketSignalingClient`、`SignalingConnectionLoop`、`ConnectionNegotiator`），不起子进程。验证"消息真的在两端之间流动"。

## Component Design

### 1. SignalingTestCluster

```csharp
public sealed class SignalingTestCluster : IAsyncLifetime
{
    public int TcpPort { get; }      // OS-assigned ephemeral
    public int WsPort { get; }       // OS-assigned ephemeral (0 = WS disabled)
    public string Host => "127.0.0.1";

    public SignalingService Signaling { get; }
    public WebSocketSignalingService? WsSignaling { get; }

    // IAsyncLifetime: StartAsync 起服务, DisposeAsync 停
    Task IAsyncLifetime.InitializeAsync();
    Task DisposeAsync();
}
```

**端口策略**：
- TCP 信令：`SignalingService` 构造用 `IPAddress.Any, port=0`？不行——`SignalingService` 构造从 `config.SignalingPort` 读端口。需要改 `ServerConfig.SignalingPort=0`，但 `TcpListener(IPAddress.Any, 0)` 会绑定临时端口。**问题**：`SignalingService` 内部 `new TcpListener(IPAddress.Any, config.SignalingPort)`，端口 0 → OS 分配。但客户端要知道实际端口。`SignalingService` 未暴露实际端口。
  - **方案 A（改产品代码最小）**：测试用固定高位端口（如 19000+），每个测试递增或随机选一个范围内端口，失败重试。简单但有竞态。
  - **方案 B（暴露端口）**：给 `SignalingService` 加 `int ActualPort` 属性（`_listener.LocalEndpoint as IPEndPoint`）。更干净，产品代码改动极小（1 属性）。**推荐 B**。
- WS：`HttpListener` 需要 `http://+:<port>/signaling/`。临时端口同理——`HttpListener` 不支持 0。用方案 B 的临时端口分配器：先 `TcpListener(0)` 拿端口，关掉，给 WS 用（竞态窗口可接受，测试环境并发低）。

**WS/CI 可行性**：
- .NET 的 `HttpListener` 在 Linux 是托管实现（不走 OS HTTP.sys），通常**不需要 root**绑定 `http://+:<port>/`。Windows 非 admin 用 `http://localhost:<port>/`（localhost 前缀免 urlacl）。**决策**：WS 用 `http://localhost:<wsPort>/signaling/` 前缀（不是 `+`），避免权限问题。产品代码的 `WebSocketSignalingService` 现在用 `http://+:{port}/`——**需改成 `localhost` 或可配置**。这是产品代码的一个小改动，纳入本任务。
  - 若 CI 仍失败，加 `[OS-Skip]` 标记条件跳过 WS 测试（但优先让它在 CI 跑）。

**生命周期**：xunit 的 `IAsyncLifetime` + `ICollectionFixture<SignalingTestCluster>` 或每测试 new。**决策**：每测试 new（隔离性好，临时端口不冲突），用 `IAsyncLifetime`。

### 2. TestClient（信令客户端包装）

```csharp
public sealed class TestClient : IDisposable
{
    private readonly SignalingTransportBase _transport;
    public List<BaseMessage> Received { get; } = new();
    public TaskCompletionSource<BaseMessage>? WaitForMessage { get; set; }

    // TCP
    public static async Task<TestClient> ConnectTcpAsync(string host, int port);
    // WS
    public static async Task<TestClient> ConnectWsAsync(string host, int port);

    // 发消息 + 等待特定类型响应
    public Task SendAsync(BaseMessage msg);
    public async Task<T> WaitForAsync<T>(TimeSpan timeout) where T : BaseMessage;
}
```

`OnMessageReceived` → 记录到 `Received` + 完成 `WaitForMessage`。`WaitForAsync<T>` 用 `TaskCompletionSource` + `CancellationTokenSource` 超时，避免挂死。

### 3. 各测试流程

**REQ-2 TCP 注册往返**：
```
cluster = new SignalingTestCluster(wsEnabled: false); await cluster.InitializeAsync();
client = await TestClient.ConnectTcpAsync(host, tcpPort);
await client.SendAsync(new RegisterMessage { NodeId = "test-node", ... });
ack = await client.WaitForAsync<RegisterAck>(timeout: 5s);
Assert.True(ack.Success);
```

**REQ-3 WS 注册往返**：同上但 `ConnectWsAsync`。`WebSocketSignalingService` 启用。验证出站桥接（曾断过的路径）。

**REQ-4 auto 回退**：只起 WS 服务，TCP 端口不监听。`SignalingConnectionLoop(transportType: "auto")`。客户端应 5s 内（TCP 超时）回退到 WS，收到 `RegisterAck`。

**REQ-5 共存**：起 TCP+WS。`tcpClient` 和 `wsClient` 各注册不同 NodeId。各等各的 `RegisterAck`，不串扰（按 clientId 路由）。

**REQ-6 连接协商**：两个客户端（扮演 Extranet + Intranet）注册到同一 cluster。Extranet 发 `ConnectRequest(TargetNodeId=intranetId)`。断言：intranet 收到 `HolePunchStart`，extranet 收到 `ConnectReady`。这测服务端中介路由，不打真实 UDP。

**REQ-7 中继**：Extranet 发 `RelayRequest`。断言双方收到 `RelayAccept`（role 分别为 extranet/intranet）。

**REQ-8 限速生效**：
```
negotiator = ... (拿到 PeerTransportSession)
negotiator.SetRateLimit(new TokenBucket(100_000)); // 100 KB/s
sw = Stopwatch.StartNew();
await negotiator.SendAsync(bigBuffer, 0, 1_000_000); // 1 MB
sw.Stop();
Assert.InRange(sw.Elapsed.TotalSeconds, 8.0, 15.0); // ~10s ±容差
```
直接对 `PeerTransportSession` 测（不需完整 P2P）。验证 `SetRateLimit` 真的接上。`SendCoreAsync` 会因没连 KCP 而走 relay/none 分支——可能需要 mock 或用 relay session。**待 implement 阶段确认 PeerTransportSession 能否在无 KCP/relay 时测限速**（桶检查在 SendCoreAsync 之前，所以即使 SendCoreAsync 空转，桶仍消耗——可测）。

### 4. 超时保护
- 每个测试 `[Fact(Timeout = 30000)]`（xunit）或内部 `CancellationTokenSource(30s)`。
- `WaitForAsync` 自带超时，超时抛 `TimeoutException` → 测试失败（而非挂死）。

## 产品代码改动（最小）

1. `SignalingService`：加 `public int ActualPort` 属性（暴露 `_listener` 实际端口）。用于测试获取端口。**向后兼容**（纯加属性）。
2. `WebSocketSignalingService`：构造的 URL 前缀从 `http://+:{port}/` 改为 `http://localhost:{port}/`（CI 友好）。**行为变化**：从绑定所有 NIC 改为仅 localhost。对生产：信令服务通常在反向代理后或单机部署，localhost 可接受；若需外部访问，文档建议反代。在 PRD 里标注为已接受的改动。
   - 或者：加 `bool bindAllNics` 参数（默认 true 生产，测试传 false）。**推荐加参数**，不改生产默认行为。

## Compatibility

- 不改现有产品行为（除 WS 绑定参数化，默认不变）。
- 新增测试独立运行，不影响现有 56 个单测。
- 临时端口隔离，支持并行。

## Rollback

测试在独立测试类，删掉即回滚。产品代码改动仅 1 属性 + 1 可选参数，纯加法。

## Native AOT

测试项目不做 AOT 发布，不受影响。产品代码改动（属性+参数）AOT 兼容。
