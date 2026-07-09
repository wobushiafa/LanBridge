# Design: 多隧道多目标

## Architecture Overview

采用 **方案 B：多 ConnectionNegotiator + 共享基础设施** 组合模式。

```
ExtranetPeer
  ├── SharedUdpStack (新增)
  │     ├── UdpHolePuncher (共享，多远端 KCP conv 分用)
  │     └── LanDiscoveryService (共享)
  ├── SharedSignalingStack (新增)
  │     ├── SignalingConnectionLoop (共享，单 TCP 连接)
  │     └── SignalingMessageDispatcher (新增，替代 SignalingMessageRouter)
  ├── TunnelRouter (新增)
  │     ├── _negotiators: { nodeId → ConnectionNegotiator }
  │     └── _mappingRoutes: { localPort → nodeId }
  ├── ConnectionNegotiator (intranet-peer-001) — 轻量，只管单目标
  ├── ConnectionNegotiator (intranet-peer-002)
  └── ConnectionNegotiator (intranet-peer-003)
```

## Component Design

### 1. SharedUdpStack (新增)

从 `ConnectionNegotiator` 中提取的共享 UDP 基础设施。

```csharp
public sealed class SharedUdpStack : IDisposable
{
    public UdpHolePuncher HolePuncher { get; }
    public LanDiscoveryService? LanDiscovery { get; }
    public IPEndPoint? PublicEndPoint { get; }
    public IPEndPoint? PublicEndPointV6 { get; }
    public NatDetectionResult? NatDetection { get; }

    public SharedUdpStack(int udpPort, string nodeId, bool verbose);

    public Task DetectNatAsync();  // 一次 STUN 探测，结果共享
    public void StartLanDiscovery(ConnectionNegotiator negotiator);
}
```

**关键**：`UdpHolePuncher` 已经有 `_kcpSessions` 字典按 conv 分用，`_remoteEndPoint` 需改为 `ConcurrentDictionary<string, IPEndPoint>` 以支持多远端。`OnHolePunched` 事件需要携带远端标识信息以便路由到正确的 Negotiator。

### 2. SharedSignalingStack (新增)

共享的信令连接和消息分发。

```csharp
public sealed class SharedSignalingStack : IDisposable
{
    public SignalingConnectionLoop ConnectionLoop { get; }

    public SharedSignalingStack(string host, int port, ...);

    // 注册消息处理器：每个 Negotiator 为自己的目标 nodeId 注册
    public void RegisterHandler(string nodeId, ISignalingHandler handler);
    public void UnregisterHandler(string nodeId);

    // 消息分发：收到 ConnectReady/HolePunchStart/RelayAccept 时
    // 根据 sessionId 路由到对应的 Negotiator handler
}
```

**消息路由策略**：信令消息中 `ConnectReady.SessionId` 和 `HolePunchStart.SessionId` 已存在。每个 `ConnectionNegotiator` 发起 `ConnectRequest` 时生成 `sessionId` 并注册到 dispatcher；dispatcher 收到响应后按 `sessionId` 路由。

### 3. ConnectionNegotiator (改造 — 轻量化)

从"全功能单例"变为"单目标轻量协调器"。

**移除**（提升到共享层）：
- `_holePuncher` 字段 → 注入 `SharedUdpStack`
- `_signalingConnectionLoop` → 注入 `SharedSignalingStack`
- `_lanDiscovery` → 由 `SharedUdpStack` 管理
- NAT 检测逻辑 → 由 `SharedUdpStack.DetectNatAsync()` 一次性完成

**保留**（单目标逻辑）：
- `_sessions` (每个 Negotiator 管自己的 PeerTransportSession)
- `_pendingPunches` (自己的打洞状态)
- `_isHolePunching` (自己的打洞标志)
- `_activeSessionId` (自己的会话 ID)
- `RequestConnectionAsync()` — 向自己的 `TargetNodeId` 发起连接
- `HandleConnectReadyAsync()` / `HandleHolePunchStartAsync()` — 处理自己的信令响应
- `HandleRelayAcceptAsync()` — 处理自己的 Relay

**构造函数变化**：
```csharp
// 旧
public ConnectionNegotiator(PeerConnectionOptions options)

// 新
public ConnectionNegotiator(
    PeerConnectionOptions options,
    SharedUdpStack udpStack,
    SharedSignalingStack signalingStack)
```

### 4. TunnelRouter (新增)

ExtranetPeer 的映射路由层。

```csharp
public sealed class TunnelRouter
{
    // 每个 mapping 绑定到哪个 nodeId
    private readonly Dictionary<int, string> _localPortToNodeId;
    // 每个 nodeId 对应的 Negotiator
    private readonly ConcurrentDictionary<string, ConnectionNegotiator> _negotiators;

    public TunnelRouter(ClientConfig config, SharedUdpStack udpStack, SharedSignalingStack signalingStack);

    // 根据 localPort 查找目标 Negotiator
    public ConnectionNegotiator? GetNegotiatorForLocalPort(int localPort);
    // 根据 sessionId 查找源 Negotiator（用于数据回调路由）
    public ConnectionNegotiator? GetNegotiatorBySessionId(string sessionId);
}
```

### 5. TunnelMapping 扩展

```csharp
public class TunnelMapping
{
    public int LocalPort { get; set; }
    public string TargetHost { get; set; } = string.Empty;
    public int TargetPort { get; set; }
    public string Protocol { get; set; } = "tcp";
    public string? TargetNodeId { get; set; }  // 新增：@nodeId 语法
}
```

### 6. UdpHolePuncher 多远端改造

**当前**：`_remoteEndPoint` 是单个 `IPEndPoint?`

**改为**：
```csharp
// 按远端地址 key 索引
private readonly ConcurrentDictionary<string, IPEndPoint> _remoteEndPoints = new();

// 注册远端（打洞开始时）
public void RegisterRemoteEndPoint(string nodeId, IPEndPoint endpoint);

// 获取远端（KCP/Unreliable 发送时）
public IPEndPoint? GetRemoteEndPoint(string nodeId);
```

`OnHolePunched` 事件增加 `string nodeId` 参数，以便路由到正确的 Negotiator。

### 7. SignalingMessageDispatcher (替代 SignalingMessageRouter)

**当前**：`SignalingMessageRouter` 直接回调 4 个 handler，无法区分多个 Negotiator。

**改为**：
```csharp
public sealed class SignalingMessageDispatcher
{
    // sessionId → handler
    private readonly ConcurrentDictionary<string, ISignalingHandler> _sessionHandlers = new();
    // nodeId → handler (for ConnectRequest 发起方匹配)
    private readonly ConcurrentDictionary<string, ISignalingHandler> _nodeHandlers = new();

    public void RegisterSession(string sessionId, ISignalingHandler handler);
    public void RegisterNode(string nodeId, ISignalingHandler handler);

    public async Task DispatchAsync(string message);
}
```

`DispatchAsync` 逻辑：
- `RegisterAck` → 广播给所有 handler（或按 nodeId 路由）
- `ConnectReady` → 按 `sessionId` 路由
- `HolePunchStart` → 按 `sessionId` 路由
- `RelayAccept` → 按 `sessionId` 路由
- `Error` → 按 `sessionId` 路由（如果可关联），否则广播

## Data Flow

### 发送路径（ExtranetPeer → IntranetPeer）

```
本地客户端连接 localPort:8554
  → TunnelRouter.GetNegotiatorForLocalPort(8554) → negotiator-001
  → negotiator-001.EnsureConnectedAsync()
  → negotiator-001.SendAsync(TunnelFrame.Open(streamId, target))
  → SharedUdpStack.HolePuncher → KCP conv-001 → 远端 intranet-peer-001
```

### 接收路径（IntranetPeer → ExtranetPeer）

```
SharedUdpStack.HolePuncher 收到 KCP 数据
  → 按 conv 分用到 KcpSession
  → KcpSession.OnDataReceived → PeerTransportSession.OnDataReceived
  → ConnectionNegotiator.OnSessionDataReceived(sessionId, data, length)
  → ExtranetPeer.HandleTunnelData → 按 streamId 路由到本地客户端
```

### 信令路径

```
ExtranetPeer 启动
  → SharedSignalingStack 连接信令服务器
  → 对每个目标 nodeId:
      → 创建 ConnectionNegotiator(nodeId, sharedUdp, sharedSignaling)
      → negotiator.RegisterNodeAsync() (仅第一个需要注册，后续直接 RequestConnection)
      → negotiator.RequestConnectionAsync()
  → SignalingServer 返回 ConnectReady(sessionId=xxx)
      → SignalingMessageDispatcher 按 sessionId 路由到对应 negotiator
      → negotiator.HandleConnectReadyAsync() → 开始打洞
```

## Compatibility & Migration

### 向后兼容

- `TunnelMapping.TargetNodeId` 默认为 `null`，此时使用 `ClientConfig.TargetNodeId` 作为默认值
- 单目标配置（不使用 `@nodeId`）下，只创建一个 `ConnectionNegotiator`，行为与改造前完全一致
- `IntranetPeer` 端**零改动** — 已按 `(sessionId, streamId)` 隔离
- `SignalingServer` 端**零改动** — 已按 `nodeId` 索引节点，多个 `ConnectRequest` 独立处理

### 信令协议

**零变更**。`ConnectRequest` 已有 `TargetNodeId` 字段，`ConnectReady`/`HolePunchStart`/`RelayAccept` 已有 `SessionId` 字段。多目标只是 ExtranetPeer 发起多个 `ConnectRequest`，每个指向不同的 `TargetNodeId`，服务端独立处理。

### Native AOT 兼容

- 新增类型不使用反射，JSON 序列化通过 Source Generator 处理
- `TunnelMapping.TargetNodeId` 需加入 `ExtranetConfigJsonContext` 的 `[JsonSerializable]` 列表

## Trade-offs

| 决策 | 收益 | 代价 |
|------|------|------|
| 共享 UDP socket | 节省端口，NAT 映射只需一个 | UdpHolePuncher 需多远端改造，并发打洞逻辑更复杂 |
| 多 Negotiator 实例 | 独立生命周期，代码改动最小 | 每个实例有自己的 _sessions 字典，内存略增 |
| 共享信令连接 | 节省 TCP 连接，服务端只需一个 client slot | 消息分发需 sessionId 路由层 |
| IntranetPeer 零改动 | 降低部署风险 | 多 ExtranetPeer 连同一 IntranetPeer 时，IntranetPeer 无法区分来源（已有 session 隔离足够） |

## Rollback

如果多目标功能出现问题：
1. ExtranetPeer 降级到单 `--target-node` 配置，所有 mapping 不带 `@nodeId`
2. 此时只创建一个 `ConnectionNegotiator`，行为等价于改造前
3. `SharedUdpStack` / `SharedSignalingStack` 在单目标模式下退化为直接注入，无额外开销
