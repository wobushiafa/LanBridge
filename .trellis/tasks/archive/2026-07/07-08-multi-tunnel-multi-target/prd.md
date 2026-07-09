# 多隧道多目标：ExtranetPeer 同时连接多个 IntranetPeer

## Goal

让一个 ExtranetPeer 可以同时建立到多个 IntranetPeer 的隧道，每个本地映射端口可路由到不同的目标节点，实现一对多穿透。

## Background

### 当前架构约束（已从代码确认）

1. **ExtranetPeer 持有单个 ConnectionNegotiator** — `ExtranetPeer.cs:128` 构造时创建一个 `_connection`，其 `PeerConnectionOptions.TargetNodeId` 为单一字符串。
2. **ConnectionNegotiator 持有单个 UdpHolePuncher** — `_holePuncher` 是单例，`_remoteEndPoint` 是单个远端地址。打洞完成后只能绑定一个 KCP 会话。
3. **PeerTransportSession 是 1:1** — 每个 session 绑定一个 KcpSession 或 RelayClient。
4. **SessionId 路由已存在** — `ConnectionNegotiator._sessions` 是 `ConcurrentDictionary<string, PeerTransportSession>`，`IntranetPeer` 已按 `(sessionId, streamId)` 管理目标连接（`StreamKey`）。这意味着 **IntranetPeer 端已经具备多会话能力**。
5. **TunnelFrame 无目标节点信息** — 帧头 16 字节（Magic + Version + Type + Reserved + StreamId + PayloadLength），没有 `targetNodeId` 字段。路由依赖 `StreamId` 唯一性和 `SessionId` 上下文。
6. **SignalingServer 按 nodeId 索引** — `_nodes` 字典以 `nodeId` 为 key，每个 IntranetPeer 注册后可被任意 ExtranetPeer 的 `ConnectRequest` 找到。
7. **ExtranetPeer 的 Mappings 已支持多端口** — `TunnelMapping` 列表可映射多个 `localPort → targetHost:targetPort:protocol`，但所有映射都走同一个隧道到同一个 IntranetPeer。

### 关键设计矛盾

- **UdpHolePuncher 是单远端的** — 一个 UDP socket 只能打洞到一个远端（`_remoteEndPoint`）。如果 ExtranetPeer 要连 3 个 IntranetPeer，需要 3 个独立的打洞/传输路径。
- **Relay 是 session-based** — 每个 Relay 连接已有独立 `sessionId`，多目标在 Relay 模式下天然可行。
- **P2P 模式下需要多个 KCP conv** — 每个远端 IntranetPeer 需要独立的 KCP 会话（不同 conv），可以共享一个 UDP socket（通过 conv 分用）或使用独立 socket。

## Requirements

### REQ-1: 多目标映射配置

ExtranetPeer 的 `--map` / `mappings` 配置需支持指定目标节点：

```
-m 8554=192.168.7.230:554:tcp@intranet-peer-001
-m 9999=192.168.8.100:9999:udp@intranet-peer-002
-m 8080=10.0.0.5:80:tcp@intranet-peer-003
```

未指定 `@nodeId` 的映射使用 `--target-node` 作为默认目标节点，保持向后兼容。

### REQ-2: 多隧道并行建立

ExtranetPeer 可同时向多个 IntranetPeer 发起 `ConnectRequest`，每个目标节点建立独立的传输路径（P2P 或 Relay）。

### REQ-3: 帧路由

从本地代理接收的数据需根据映射配置路由到正确的目标节点隧道；从远端接收的数据需根据 `StreamId` 路由回正确的本地客户端。

### REQ-4: 向后兼容

- 现有单目标配置（`--target-node` + 不带 `@nodeId` 的 mappings）必须继续工作
- IntranetPeer 端无需改动（已按 session 隔离）
- 信令协议仅新增字段，不破坏旧版

### REQ-5: 独立生命周期

每个目标节点的隧道独立管理：一个隧道断开不影响其他隧道；每个隧道独立重连。

## Acceptance Criteria

- [ ] ExtranetPeer 可配置多个目标节点的映射（`@nodeId` 语法）
- [ ] 不带 `@nodeId` 的映射使用 `--target-node` 默认值
- [ ] 可同时与 2+ 个 IntranetPeer 建立隧道
- [ ] 每个隧道可独立选择 P2P 或 Relay 传输模式
- [ ] 单个隧道断开后其他隧道不受影响
- [ ] 现有单目标配置（无 `@nodeId`）行为不变
- [ ] `dotnet test` 全部通过
- [ ] `dotnet build -c Release` 无错误

## Out of Scope

- IntranetPeer 同时服务多个 ExtranetPeer 的访问控制（已有 session 隔离）
- 负载均衡 / 自动选路（远期特性）
- 跨节点聚合带宽

## Resolved Decisions

- **D1: P2P 模式使用共享 UDP socket + KCP conv 分用** — 一个 UdpHolePuncher 管理多个远端，通过 conv 区分 KCP 会话。节省端口，NAT 映射只需一个，STUN 探测只需一次。
- **D2: STUN/NAT 检测共享** — 共享 socket 自然共享检测结果，每个远端隧道不需要独立 NAT 探测。
- **D3: 采用方案 B（多 ConnectionNegotiator + 共享 UdpHolePuncher/Signaling）** — 每个 ConnectionNegotiator 只管一个目标节点，保持单目标语义；UdpHolePuncher 和 SignalingConnectionLoop 提升为共享组件注入；新增 TunnelRouter 路由 mapping → negotiator。独立生命周期天然实现。

## Open Questions

_(None remaining — all product decisions resolved)_
