# P2P/KCP 数据通路集成测试

## Goal

为 LanBridge 最复杂、最易碎且**当前零集成测试**的路径——P2P KCP 数据通路——建立集成测试。验证打洞/KCP 会话建立后数据真的双向流动。扩展现有集成测试框架（`SignalingTestCluster`/`TestClient`）到数据面。

## Background

现有集成测试覆盖**信令往返 + 限速**，但 `NegotiationFlowTests` 只验证 `ConnectRequest→HolePunchStart` 信令到达，**不验证打洞成功后 KCP 数据真的流动**。`KcpSession`/`PeerTransportSession` 的数据面（`Send`→KCP→UDP→对端 receive→`OnDataReceived`）整条链没有测试。这是仓库风险最高的未测路径——KCP 的可靠性/顺序/拥塞/分片逻辑回归会无声发生。

## Scope

### REQ-1: KCP loopback 双向数据流（核心）
两个 `UdpClient` 绑 `IPAddress.Loopback` 临时端口，两个 `KcpSession`（同 conv 互指对端 endpoint，`ownReceiveLoop:true`）。`Start()` 后 `sessionA.Send(payload)` → 断言 `sessionB.OnDataReceived` 收到 payload。反向亦然。验证 KCP 协议层（3 次握手、分片重组、可靠性）。

### REQ-2: PeerTransportSession + KCPSession 集成
两个 `PeerTransportSession`，各 `UseP2p(kcpSession)` 挂载。`peerA.SendAsync(payload)` → 断言 `peerB.OnDataReceived` 收到。验证数据面 facade（`SendAsync`→`SendCoreAsync`→`kcp.Send`）+ 事件转发链（`KcpSession.OnDataReceived`→`PeerTransportSession.OnDataReceived`）。

### REQ-3: 限速在 P2P 路径生效（与 bandwidth-qos 交叉）
`peerA.SetRateLimit(new TokenBucket(R))`，发 N 字节（N >> R），断言耗时 ≥ N/R。验证 `PeerTransportSession.SendAsync` 的桶检查在真实 KCP 路径上也生效（不只是空转 SendCoreAsync）。

### REQ-4: 大消息分片重组
发送 > MTU 的消息（如 100KB），断言对端收到的数据与发送的完全一致（长度 + 内容）。验证 KCP 分片/重组正确性。

## Acceptance Criteria

- [ ] REQ-1: KCP loopback 双向数据流测试通过
- [ ] REQ-2: PeerTransportSession+KcpSession 集成测试通过
- [ ] REQ-3: P2P 路径限速生效测试通过（耗时在容差内）
- [ ] REQ-4: 大消息分片重组测试通过（内容一致）
- [ ] 所有测试有超时保护（`[Fact(Timeout=...)]` + WaitForAsync），无挂死
- [ ] `dotnet build -c Release` 0 警告，`dotnet test` 全绿（64 + 新增，两轮无 flakiness）
- [ ] 测试在 loopback，无需管理员权限/真实 NAT

## Out of Scope

- **真实 UDP 打洞握手**（`UdpHolePuncher.StartPunchingAsync` 的 stun/端口预测）——需 NAT 模拟，太难。REQ-1 直接构造 KcpSession 跳过握手。
- **完整 ConnectionNegotiator 端到端**（信令→打洞→KCP）——REQ-1/2 已覆盖数据面链路，全链路留后续。
- **KCP 拥塞控制/丢包恢复**——需模拟丢包，太复杂；只测正常路径。
- **中继（RelayClient）数据通路**——另开。

## Resolved Decisions

- **D1: 直接构造 KcpSession 跳过打洞握手**——测 KCP 数据通路不需要真实打洞；两个 UdpClient 互指即可。
- **D2: 复用现有 `EphemeralPortHelper`**——临时端口分配已在集成测试框架里。
- **D3: loopback + 同 conv**——KCP 要求两端 conv 一致；loopback 无 NAT 简化。

## Open Questions

- KCP 3 次握手需要短暂时间才 IsConnected；测试发 Send 前是否要等 IsConnected？KCP 的 `Send` 在未握手时也应触发握手并最终送达——测试用 `WaitForAsync` 超时等 OnDataReceived 即可，不必先等 IsConnected。
