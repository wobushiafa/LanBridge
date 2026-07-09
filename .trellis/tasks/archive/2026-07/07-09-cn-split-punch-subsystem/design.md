# Design: NAT/打洞子系统抽取

## Architecture

`PeerPunchCoordinator` 持有 hole puncher + NAT 状态，通过**事件出射**通知控制面/数据面，通过**回调委托入射**获取控制面/数据面能力。无循环依赖（不持 negotiator 引用）。

```
ConnectionNegotiator (control plane: signaling handlers, RequestConnection/Register/Relay)
  ├── _sessions: PeerSessionManager (data plane, Phase 1)
  └── _punch: PeerPunchCoordinator (NAT/hole-punch, Phase 2)  ← 新
        ├── _holePuncher, _pendingPunches, _isHolePunching, _relayProbeCts
        ├── _publicEndPoint/_publicEndPointV6, _natDetection, _natDiagnostics, _lanDiscovery
        ├── events OUT: OnStatusChanged/OnModeChanged/OnP2pUnhealthy/OnUnreliableDataReceived
        └── callbacks IN: AttachP2p, IsConnected, ActiveSessionIdProvider, IsSignalingConnected,
                          RequestRelayAsync, RequestConnectionAsync, RegisterNodeAsync
```

## Component Design

### 1. PeerPunchCoordinator (新)

```csharp
public sealed class PeerPunchCoordinator : IDisposable
{
    // state (搬自 negotiator)
    private UdpHolePuncher? _holePuncher;
    private readonly ConcurrentDictionary<string, PendingPunch> _pendingPunches = new();
    private IPEndPoint? _publicEndPoint, _publicEndPointV6;
    private NatDetectionResult? _natDetection;
    private readonly PeerNatDiagnostics _natDiagnostics;
    private LanDiscoveryService? _lanDiscovery;
    private CancellationTokenSource? _relayProbeCts;
    private volatile bool _isHolePunching;

    // events OUT
    public event Action<string>? OnStatusChanged;
    public event Action<PeerTransportMode>? OnModeChanged;
    public event Action<string,string>? OnP2pUnhealthy;
    public event Action<byte[],int,string>? OnUnreliableDataReceived;  // data, len, sessionId

    // callbacks IN (set via StartAsync or ctor)
    private readonly Action<string, KcpSession> _attachP2p;
    private readonly Func<bool> _isConnected;
    private readonly Func<string> _activeSessionIdProvider;
    private readonly Func<bool> _isSignalingConnected;
    private readonly Func<Task> _requestRelayAsync;
    private readonly Func<bool, Task> _requestConnectionAsync;
    private readonly Func<Task> _registerNodeAsync;

    public PeerPunchCoordinator(
        PeerConnectionOptions options,
        Action<string, KcpSession> attachP2p,
        Func<bool> isConnected,
        Func<string> activeSessionIdProvider,
        Func<bool> isSignalingConnected,
        Func<Task> requestRelayAsync,
        Func<bool, Task> requestConnectionAsync,
        Func<Task> registerNodeAsync);

    public IPEndPoint? PublicEndPoint => _publicEndPoint;
    public IPEndPoint? PublicEndPointV6 => _publicEndPointV6;

    public Task StartAsync(SharedUdpStack? injectedStack);  // 建/取 holePuncher + NAT detect + ConfigureHolePuncherEvents + LAN discovery
    public Task BeginHolePunchFromSignalAsync(string sessionId, string? target, string? targetV6, uint conv, StunNatType? targetNatType, string statusTemplate);
    public Task<bool> EnsureConnectedAsync(TimeSpan timeout, CT ct);  // 搬自 negotiator
    public Task SendUnreliableAsync(byte[] data, int off, int len);                   // active
    public Task SendUnreliableAsync(string sessionId, byte[] data, int off, int len); // 用 _holePuncher + session 桶
    public Task HandleLanDiscoveryRequestAsync(IPEndPoint client, uint conv);
    public void StartNatKeepAliveLoop(CT ct);
    public void StartRelayProbeLoop();  // 内部回调 RequestConnectionAsync
    public UdpHolePuncher? GetHolePuncher();
    public void Dispose();
}
```

**ConfigureHolePuncherEvents**（原样搬，把直接访问 negotiator 字段替换为回调/事件）：
- `OnHolePunched` → 建 `KcpSession(conv, _holePuncher.Client, endpoint, ...)`，`_attachP2p(pending.SessionId, session)`，`_holePuncher.RegisterKcpSession(session)`，若 `pending.SessionId == _activeSessionIdProvider()` 则 `OnModeChanged?.Invoke(P2pDirect)`，`_relayProbeCts?.Cancel()`。
- `OnLanAdvertised` → `if (_isConnected()) return;` sessionId = `_activeSessionIdProvider()`，`_pendingPunches[...] = ...`，`_holePuncher.TriggerHolePunched(...)`。
- `OnError` → `OnStatusChanged?.Invoke(...)`。
- `OnUnreliableDataReceived` → 校验 endpoint == RemoteEndPoint，`OnUnreliableDataReceived?.Invoke(buffer, length, _activeSessionIdProvider())`。

**BeginHolePunchFromSignalAsync**：原样搬，relay fallback 用 `_requestRelayAsync()` 回调；`_isHolePunching`/`_pendingPunches`/`_natDetection` 都在本类；`OnModeChanged?.Invoke(None)`/`OnStatusChanged`。

**StartNatKeepAliveLoop**：原样搬，`shouldProbe: () => _isSignalingConnected() && !_isConnected() && !_isHolePunching`，`onMappingChangedAsync: async ep => { _publicEndPoint = ep; await _registerNodeAsync(); }`。

**StartRelayProbeLoop**：原样搬，`Mode == Relay` 改为读 `_isConnected()`+通过外部状态（或加 `Func<PeerTransportMode> modeProvider` 回调）——核对：probe loop 检查 `Mode == Relay`。需加 `Func<PeerTransportMode> _modeProvider` 回调，或 coordinator 暴露 `IsRelayMode` 由外部传。**决策**：加 `Func<PeerTransportMode> modeProvider` 到回调集（共 8 个回调——略多，但仍可接受；或合并到 isConnected 风格）。

### 2. ConnectionNegotiator 改编排

字段：`private readonly PeerPunchCoordinator _punch;`（替代上述 NAT/打洞字段）。

ctor：`_punch = new PeerPunchCoordinator(options, attachP2p: (id, s) => _sessions.GetSession(id).UseP2p(s), isConnected: () => IsConnected, activeSessionIdProvider: () => _sessions.ActiveSessionId, isSignalingConnected: () => IsSignalingConnected, requestRelayAsync: RequestRelayAsync, requestConnectionAsync: RequestConnectionAsync, registerNodeAsync: RegisterNodeAsync, modeProvider: () => Mode);`

`StartAsync`：删 NAT/打洞编排代码，改为 `_punch.StartAsync(_injectedUdpStack)` + 接事件转发（`_punch.OnStatusChanged += s => OnStatusChanged?.Invoke(s)` 等）+ 原有信令触发（RequestConnection/Register）。

委托成员（签名不变）：
- `PublicEndPoint`/`PublicEndPointV6` → `=> _punch.PublicEndPoint`
- `SendUnreliableAsync`×2 → `=> _punch.SendUnreliableAsync(...)`
- `HandleLanDiscoveryRequestAsync` → `=> _punch.HandleLanDiscoveryRequestAsync(...)`
- `EnsureConnectedAsync` → `=> _punch.EnsureConnectedAsync(...)`
- `GetHolePuncher()` 若有外部调用——核对（`SharedUdpStack.StartLanDiscovery(ConnectionNegotiator)` 不直接调 GetHolePuncher；`HandleLanDiscoveryRequestAsync` 已委托）。若外部无调用，GetHolePuncher 可删或留委托。

控制面 handler：
- `HandleConnectReadyAsync`/`HandleHolePunchStartAsync` → `_activeSessionId` 用 `_sessions.SetActiveSession`（Phase 1 已改），调 `_punch.BeginHolePunchFromSignalAsync(...)`。
- `HandleRelayAcceptAsync` → `_sessions.GetSession(sessionId).UseRelay(...)`（Phase 1 已改），`StartRelayProbeLoop()` → `_punch.StartRelayProbeLoop()`，`OnModeChanged?.Invoke(Relay)` → 仍直接 raise（控制面事件）或通过 `_punch`？**决策**：`OnModeChanged` 是 negotiator 的事件，handler 直接 raise 即可（relay 模式由控制面决定）。
- `HandleP2pUnhealthyAsync` → 搬到 `_punch`（它有 OnP2pUnhealthy 事件 + RequestRelayIfAllowed 回调）。或留 negotiator 调 `_punch` 的方法。**决策**：`HandleP2pUnhealthyAsync` 留 negotiator（PeerSessionManager 的 OnP2pUnhealthy 事件接到 negotiator，negotiator 调 RequestRelayIfAllowedAsync——控制面动作）。coordinator 不参与 P2P unhealthy 处理。

### 3. 不变项
- `ISignalingHandler` 仍在 `ConnectionNegotiator`。
- `LanDiscoveryService` ctor 仍接 `ConnectionNegotiator`——保持（Phase 2 不动这个注入点；coordinator 内部建 `_lanDiscovery` 时仍传 `this`[negotiator]——核对：standalone 模式 `_lanDiscovery = new LanDiscoveryService(_options.NodeId, this, ...)`，`this` 是 negotiator。coordinator 建时需要 negotiator 引用……**这是循环依赖点**）。

**循环依赖处理**：`LanDiscoveryService` 需要一个 `ConnectionNegotiator`（调 `HandleLanDiscoveryRequestAsync` 等）。若 coordinator 建 LanDiscoveryService，它需要 negotiator 引用。**方案**：coordinator 不建 LanDiscoveryService；negotiator 在 StartAsync 里建（`_lanDiscovery = new LanDiscoveryService(_options.NodeId, this, ...)`，`this`=negotiator），但把 LAN 发现的**编排**（`StartLanDiscoveryBroadcast`/`HandleLanDiscoveryRequestAsync`）委托 coordinator（用 coordinator 的 `_holePuncher`）。或：`LanDiscoveryService` 改接接口而非具体 negotiator（Phase 3 再做）。**Phase 2 决策**：`_lanDiscovery` 留在 negotiator（避免循环依赖），coordinator 只接手 hole puncher 编排 + NAT + 打洞 + relay probe。LAN 发现编排（broadcast/handle request）留 negotiator，通过 `_punch.GetHolePuncher()` 拿 puncher。

## Compatibility

- public API 100% 不变。
- 行为等价（纯重构）。
- 调用方零改动。

## Rollback

`PeerPunchCoordinator.cs` 新文件，删掉即回滚。negotiator 改动内联回退。

## Native AOT

sealed class，委托用 `Action`/`Func`（AOT 友好，无反射）。无接口虚调用。

## Risk

- **中**：子系统耦合深，回调接缝多（~8 个委托）。主要风险：事件/回调接线遗漏导致行为漂移（如 relay fallback 不触发、keepalive 不重注册）。
- 缓解：集成测试覆盖 P2P/relay 路径（`NegotiationFlowTests` 的 ConnectRequest/HolePunchStart/RelayAccept 往返）+ `auto` 回退测试。两轮测试确认无回归。
- `LanDiscoveryService` 循环依赖：通过"LAN 发现编排留 negotiator"规避（见 §3）。
