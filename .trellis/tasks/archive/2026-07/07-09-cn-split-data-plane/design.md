# Design: ConnectionNegotiator 拆分阶段 1（数据面抽取）

## Architecture

`ConnectionNegotiator` 从"既管协商又管数据"变为"控制面委托数据面"。

```
ConnectionNegotiator (control plane: signaling handlers, NAT/hole-punch, LAN discovery)
  ├── _sessions: PeerSessionManager  ← 新（数据面）
  │     ├── _sessions: ConcurrentDictionary<string, PeerTransportSession>
  │     ├── _activeSessionId
  │     ├── GetSession / SetActiveSession
  │     ├── SendAsync / SendHighPriorityAsync (×2)
  │     ├── Mode / IsConnected / GetStatsSnapshot
  │     └── OnDataReceived / OnSessionDataReceived (转发)
  ├── _signalingConnectionLoop (不变)
  ├── _holePuncher / _natDiagnostics / _pendingPunches (不变，后续阶段抽)
  └── SendUnreliableAsync (留此，通过 _sessions.GetSession 调桶)
```

## Component Design

### 1. PeerSessionManager (新)

```csharp
public sealed class PeerSessionManager : IDisposable
{
    private readonly PeerConnectionOptions _options;
    private readonly ConcurrentDictionary<string, PeerTransportSession> _sessions = new();
    private string _activeSessionId = "default";

    public event Action<byte[], int>? OnDataReceived;
    public event Action<string, byte[], int>? OnSessionDataReceived;

    public PeerSessionManager(PeerConnectionOptions options);

    public PeerTransportSession GetSession(string sessionId);  // factory: 创建+接QoS+接事件
    public void SetActiveSession(string sessionId);             // control plane 改 active
    public string ActiveSessionId => _activeSessionId;

    public PeerTransportMode Mode => GetSession(_activeSessionId).Mode;
    public bool IsConnected => GetSession(_activeSessionId).IsConnected;

    public Task SendAsync(byte[] data, int offset, int length);                 // active
    public Task SendAsync(string sessionId, byte[] data, int offset, int length);
    public Task SendHighPriorityAsync(byte[] data, int offset, int length);     // active
    public Task SendHighPriorityAsync(string sessionId, byte[] data, int offset, int length);

    public NegotiatorStats GetStatsSnapshot();                  // active session

    public void Dispose();
}
```

**GetSession 工厂**（从现有 `ConnectionNegotiator.GetSession` 原样搬入，事件转发到本类的事件）：
```csharp
public PeerTransportSession GetSession(string sessionId)
{
    return _sessions.GetOrAdd(sessionId, id =>
    {
        var transport = new PeerTransportSession(_options.Verbose);
        transport.SetPriority(_options.Priority);
        if (_options.RateLimitBytesPerSec > 0)
            transport.SetRateLimit(new TokenBucket(_options.RateLimitBytesPerSec));
        transport.OnDataReceived += (data, length) =>
        {
            OnDataReceived?.Invoke(data, length);
            OnSessionDataReceived?.Invoke(id, data, length);
        };
        transport.OnDisconnected += ...;  // 原样搬
        transport.OnStatusChanged += ...; // 原样搬
        return transport;
    });
}
```

**GetStatsSnapshot**：现有 `ConnectionNegotiator.GetStatsSnapshot`（行 858-870）整体搬入，读 `_activeSessionId` 的 session。

### 2. ConnectionNegotiator 改委托

字段：`private readonly PeerSessionManager _sessions;`（替代原 `ConcurrentDictionary<string, PeerTransportSession> _sessions`）。

两个 ctor 各自 `new PeerSessionManager(options)`（或注入——但 phase1 保持简单，自建）。

委托成员（签名不变）：
```csharp
public PeerTransportMode Mode => _sessions.Mode;
public bool IsConnected => _sessions.IsConnected;
public Task SendAsync(byte[] d, int o, int l) => _sessions.SendAsync(d, o, l);
public Task SendAsync(string s, byte[] d, int o, int l) => _sessions.SendAsync(s, d, o, l);
public Task SendHighPriorityAsync(byte[] d, int o, int l) => _sessions.SendHighPriorityAsync(d, o, l);
public Task SendHighPriorityAsync(string s, byte[] d, int o, int l) => _sessions.SendHighPriorityAsync(s, d, o, l);
public NegotiatorStats GetStatsSnapshot() => _sessions.GetStatsSnapshot();
```

事件转发（ctor 里接线）：
```csharp
_sessions.OnDataReceived += d => OnDataReceived?.Invoke(d, d.Length);  // 注意签名对齐
_sessions.OnSessionDataReceived += (id, d, l) => OnSessionDataReceived?.Invoke(id, d, l);
```
（核对 `OnDataReceived` 签名：`ConnectionNegotiator.OnDataReceived` 是 `Action<byte[], int>`；`PeerTransportSession.OnDataReceived` 也是 `Action<byte[], int>`。直接转发即可。）

control plane handler 改动（仅替换 `_activeSessionId = x` → `_sessions.SetActiveSession(x)`，`GetSession(s).UseRelay/UseP2p` → `_sessions.GetSession(s).UseRelay/UseP2p` 不变）：
- `HandleConnectReadyAsync:454` `_activeSessionId = sessionId` → `_sessions.SetActiveSession(sessionId)`
- `HandleHolePunchStartAsync:478`（生成新 sessionId，不改 active——核对原逻辑）
- `HandleRelayAcceptAsync:499` `_activeSessionId = sessionId` → `_sessions.SetActiveSession(sessionId)`；`:514 GetSession(sessionId).UseRelay` → `_sessions.GetSession(sessionId).UseRelay`
- `BeginHolePunchFromSignalAsync` 内 `GetSession(s).UseP2p(...)` → `_sessions.GetSession(s).UseP2p(...)`（`_sessions` 现在是 PeerSessionManager，`GetSession` 返回 PeerTransportSession，方法不变）
- 各处 `IsConnected`/`Mode`/`GetSession(_activeSessionId)` 自动走委托

`SendUnreliableAsync`（288-335）：`var session = GetSession(sessionId)` → `var session = _sessions.GetSession(sessionId)`（`GetSession` 仍 public 在 negotiator 上委托，或直接调 `_sessions.GetSession`）。行为不变。

`Dispose`：加 `_sessions.Dispose()`。

### 3. 不变项
- `ISignalingHandler` 实现仍在 `ConnectionNegotiator`。
- `LanDiscoveryService`/`SharedUdpStack` 按类型引用 `ConnectionNegotiator`——零影响（签名不变）。
- `SignalingMessageDispatcher` 用 `ISignalingHandler`——零影响。
- `RateLimitIntegrationTests.Negotiator_WiresRateLimitFromConfig` 构造 `ConnectionNegotiator` 调 `GetStatsSnapshot`——仍走 `_sessions` 工厂，行为不变。

## Compatibility

- `ConnectionNegotiator` public API 签名 100% 不变。
- 行为等价（纯重构，工厂/send/统计逻辑原样搬迁）。
- 调用方零改动。

## Rollback

`PeerSessionManager.cs` 是新文件，删掉即回滚。`ConnectionNegotiator` 的改动是内联回退（恢复字段 + 内联方法）。

## Native AOT

新类为 sealed class，无反射。`PeerConnectionOptions` 已是 record（`with` 支持）。AOT 兼容。

## Risk

- **中低**：纯重构 + 集成测试覆盖 send/会话/限速路径。
- 主要风险点：事件转发签名对齐（`OnDataReceived` 的 `(byte[], int)` vs 可能的长度参数）——implement 时核对。
- `_activeSessionId` 的并发读写：原代码 `_activeSessionId` 是普通 `string` 字段（非 volatile），本设计保持原语义（`PeerSessionManager._activeSessionId` 同为普通字段）。不引入新并发语义。
