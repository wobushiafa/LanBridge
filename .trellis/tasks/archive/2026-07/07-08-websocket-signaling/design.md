# Design: WebSocket 信令传输通道

## Architecture

在现有 TCP 信令旁增加可选的 WebSocket 监听，客户端和服务端通过传输抽象层隔离差异。

```
SignalingServer
  ├── SignalingService (TCP 9000, 不变)
  └── WebSocketSignalingService (新增, HTTP 9010 → WS Upgrade)
        └── 复用 SignalingService 的消息处理逻辑

SignalingConnectionLoop
  └── ISignalingTransport (新增接口)
        ├── SignalingClient (TCP, 不变)
        └── WebSocketSignalingClient (新增)
```

## Component Design

### 1. SignalingTransportBase (新增抽象基类)

```csharp
public abstract class SignalingTransportBase : IDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource _cts = new();

    public abstract bool IsConnected { get; }
    public event Func<string, Task>? OnMessageReceived;
    public event Action? OnDisconnected;
    public event Action<string>? OnError;

    // 子类实现：发送原始消息
    protected abstract Task SendCoreAsync(string message, CancellationToken ct);

    // 子类调用：收到消息时触发
    protected Task HandleMessageAsync(string message) => OnMessageReceived?.Invoke(message) ?? Task.CompletedTask;
    protected void HandleDisconnected() => OnDisconnected?.Invoke();
    protected void HandleError(string error) => OnError?.Invoke(error);

    // 公共发送接口（带锁保护）
    public async Task SendAsync(string message)
    {
        await _sendLock.WaitAsync(_cts.Token);
        try { await SendCoreAsync(message, _cts.Token); }
        finally { _sendLock.Release(); }
    }

    public void Dispose() { _cts.Cancel(); _cts.Dispose(); DisposeCore(); }
    protected abstract void DisposeCore();
}
```

**为什么用抽象基类而非接口**：`_sendLock`、`_cts`、事件、`SendAsync` 的锁逻辑是共用的，接口无法包含实现。Native AOT 中抽象基类的虚调用无额外问题。

### 2. WebSocketSignalingClient (新增)

```csharp
public sealed class WebSocketSignalingClient : SignalingTransportBase
{
    private ClientWebSocket? _ws;
    private readonly string _serverUrl;  // ws://host:port or wss://host:port

    public WebSocketSignalingClient(string host, int port, bool useTls = false);
    public override bool IsConnected => _ws?.State == WebSocketState.Open;

    public async Task ConnectAsync();
    protected override async Task SendCoreAsync(string message, CancellationToken ct);
    private async Task ReceiveLoopAsync();
    protected override void DisposeCore();
}
```

**帧格式**：WebSocket 文本帧，内容为 JSON 字符串（与 TCP 模式完全一致，无 4 字节长度前缀）。

**接收循环**：
```csharp
private async Task ReceiveLoopAsync()
{
    var buffer = new byte[65536];
    while (_ws?.State == WebSocketState.Open)
    {
        var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            HandleDisconnected();
            break;
        }
        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
        await HandleMessageAsync(message);
    }
}
```

### 3. WebSocketSignalingService (新增，服务端)

```csharp
public sealed class WebSocketSignalingService : IDisposable
{
    private readonly HttpListener _listener;
    private readonly SignalingService _signalingService;  // 委托消息处理
    private bool _isRunning;

    public WebSocketSignalingService(int wsPort, SignalingService signalingService);

    public async Task StartAsync(CancellationToken ct);
    // HttpListener 接受 → WebSocket 升级 → 消息转发到 SignalingService
}
```

**关键设计**：WebSocket 客户端接入后，复用 `SignalingService.ProcessMessageAsync` 处理消息。需要适配：

- SignalingService 当前通过 `clientId`（TCP 连接 ID）标识客户端
- WS 客户端也需要唯一 `clientId`
- 消息处理逻辑不变，只是接收来源从 TCP stream 变为 WS frame

**适配方式**：在 `WebSocketSignalingService` 中为每个 WS 连接创建一个适配器，将 WS 消息转发到 `SignalingService.OnMessageReceived` + `ProcessMessageAsync`。

### 4. SignalingConnectionLoop 改造

```csharp
// 旧
private SignalingClient? _client;
// 新
private SignalingTransportBase? _transport;

// 旧：创建 SignalingClient
_client = new SignalingClient(_host, _port);
// 新：根据传输类型创建
_transport = _transportType switch
{
    SignalingTransportType.Tcp => new SignalingClient(_host, _port),
    SignalingTransportType.WebSocket => new WebSocketSignalingClient(_host, _wsPort),
    _ => new SignalingClient(_host, _port)
};

// 属性
public bool IsConnected => _transport?.IsConnected == true;
public SignalingTransportBase? Transport => _transport;  // 替代 Client
```

**auto 模式**：先尝试 TCP 连接（超时 5 秒），失败后回退 WS。

### 5. SignalingClient 改造

继承 `SignalingTransportBase`，移除重复的 `_sendLock`、`_cts`、事件声明等。

```csharp
public class SignalingClient : SignalingTransportBase
{
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;

    public override bool IsConnected => ...;
    public async Task ConnectAsync();
    protected override async Task SendCoreAsync(string message, CancellationToken ct);
    private async Task ReceiveLoopAsync();
    protected override void DisposeCore();
}
```

### 6. 配置扩展

**ServerConfig**：
```csharp
public int WebSocketPort { get; set; }  // 新增，默认 0 = 不启用
```

**ServerPortOptions**：
```csharp
public int WebSocketPort { get; set; }  // 新增
```

**命令行（服务端）**：`--ws-port 9010`

**TransportOptions（客户端）**：
```csharp
public string SignalingTransport { get; set; } = "tcp";  // 新增：tcp|ws|auto
public int SignalingWsPort { get; set; } = 9010;        // 新增
```

**命令行（客户端）**：`--signaling-transport auto --ws-port 9010`

## Compatibility

- 不配置 `--ws-port` 时，`WebSocketSignalingService` 不启动，零影响
- `SignalingClient` 继承 `SignalingTransportBase` 后对外行为不变
- IntranetPeer 使用 `SignalingClient`（TCP），无需改动
- 信令消息格式零变更——WS 文本帧 = TCP JSON 内容

## Native AOT Considerations

- `System.Net.WebSockets.ClientWebSocket` 和 `HttpListener` 均在 .NET BCL 内，AOT 兼容
- `SignalingTransportBase` 为 abstract class，虚调用在 AOT 中正常
- 无反射依赖

## Rollback

不配置 WS 相关参数时，代码路径与改造前完全一致。`SignalingClient` 继承 `SignalingTransportBase` 是纯重构，行为等价。
