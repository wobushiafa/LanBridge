# 带宽限制与 QoS 优先级队列

## Goal

为 LanBridge 的数据路径增加 per-stream 带宽限制（令牌桶）和 per-mapping QoS 优先级队列，确保关键流量（如实时 UDP 视频流）不会被大流量 TCP 批量传输挤占。TCP 映射与 UDP 映射可配置不同的优先级，每个映射可独立配置速率上限。

## Background

### 当前数据路径分析

**ExtranetPeer 发送路径**（发往 IntranetPeer）:
1. 本地客户端连接 `localPort` -> `TcpListener.AcceptTcpClientAsync`
2. `HandleLocalClientAsync`: 读取 TCP 流数据 -> `TunnelFrame.WriteHeader(buffer, 0, Data, streamId, bytesRead)` 写入 16 字节头 -> `_connection.SendAsync(buffer, 0, 16 + bytesRead)`
3. `ConnectionNegotiator.SendAsync` -> `GetSession(_activeSessionId).SendAsync(data, offset, length)`
4. `PeerTransportSession.SendAsync` -> `kcp.Send(data, offset, length)` 或 `relay.SendAsync(data, offset, length)`

**UDP 发送路径**:
- `ReceiveLocalUdpPacketsAsync`: 收到 UDP 包 -> 写入 `UnreliableData` 帧头 -> `_holePuncher.Client.SendAsync(…)` 直接发送（跳过 PeerTransportSession，走原始 UDP socket）

**IntranetPeer 发送路径**（返回数据流）:
- `ReadTcpTargetLoopAsync` / `ReadUdpTargetLoopAsync` -> TunnelFrame 封装 -> `_connection.SendAsync(sessionId, buffer, 0, 16 + bytesRead)`

**关键观察**:
- 当前 `PeerTransportSession.SendAsync` 没有排队或限速，直接透传数据到 KCP/Relay
- 所有 stream 共享同一 `PeerTransportSession`（即同一 KCP 会话或 Relay 连接），无优先级区分
- 无带宽统计、无速率限制、无优先级队列
- UDP 的 UnreliableData 走 `UdpHolePuncher` 的原始 UDP socket，完全跳过 `PeerTransportSession` — QoS 需要同时覆盖

### 约束条件

- **Native AOT 兼容**：所有新增类型不可使用反射，JSON 序列化必须通过 Source Generator
- **零分配热路径**：`ArrayPool<byte>` 的使用模式必须保持，令牌桶不应产生 per-packet 堆分配
- **向后兼容**：不配置限额/优先级时，行为与现在完全一致（零开销）
- **KCP 拥塞控制交互**：KCP 已有 `cwnd`、`UseAdaptiveCongestion`、`SetNodelay`，限速层不能与 KCP 内部拥塞控制冲突

## Requirements

### REQ-1: Per-mapping 带宽上限

TunnelMapping 新增 `RateLimitBytesPerSec` 字段。配置示例：

```json
{
  "Mappings": [
    { "LocalPort": 8554, "TargetHost": "192.168.7.230", "TargetPort": 554,
      "Protocol": "tcp", "RateLimitBytesPerSec": 0 },
    { "LocalPort": 9999, "TargetHost": "192.168.8.100", "TargetPort": 9999,
      "Protocol": "udp", "RateLimitBytesPerSec": 10485760 }
  ]
}
```

- `0` 表示不限速（默认值）
- 正值表示该映射的每个 stream 的发送速率上限（字节/秒）
- 限速作用于 **ExtranetPeer 到 IntranetPeer 方向** 的发送（也是瓶颈方向）

### REQ-2: Per-mapping QoS 优先级

TunnelMapping 新增 `Priority` 字段，取值范围 0-7（0=最高，7=最低）。

- `Priority` 默认值：UDP 映射 = 2，TCP 映射 = 4
- 控制帧（`Open`、`Close`、`Error`）始终按最高优先级（0）发送，跳过队列
- `TunnelFrameType.UnreliableData` 和 `TunnelFrameType.Data` 帧按优先级排队

### REQ-3: 优先级队列调度

- **严格优先级调度**（Strict Priority Queuing）：高优先级队列中的帧全部发送完毕后才发送低优先级帧
- 在同一优先级内按 FIFO 顺序发送
- **UDP 实时流量优先于 TCP 批量流量**：UDP 映射的 `UnreliableData` 帧具有优先级 2，TCP 映射的 `Data` 帧优先级 4

### REQ-4: 零分配令牌桶

- 使用 `Stopwatch.GetTimestamp()`（monotonic clock）补充令牌，避免系统时间跳变
- 桶容量 = `max(RateLimitBytesPerSec / 10, 1500)`（最多积累 100ms 的突发量，至少一个 MTU）
- 零分配实现：使用 `long` 型桶余额和 `long` 型上次补充时间戳

### REQ-5: 限速行为

- `RateLimitBytesPerSec == 0` 时，数据传输零开销（不进入令牌桶逻辑）
- 限速时：桶余额不足时帧排队等待下次补充，等待超时 1 秒则丢弃并触发告警
- 排队帧的等待不阻塞其他高优先级帧或控制帧

### REQ-6: 向后兼容

- `TunnelMapping.RateLimitBytesPerSec` 默认 0，`Priority` 默认按协议区分（UDP=2, TCP=4）
- 旧配置文件无新增字段时行为不变
- 新增字段必须加入 `ExtranetConfigJsonContext` 的 Source Generator 范围

## Acceptance Criteria

- [ ] `TunnelMapping` 新增 `RateLimitBytesPerSec`（long, default 0）和 `Priority`（byte, default 自动推导）
- [ ] 令牌桶限速：`RateLimitBytesPerSec > 0` 时精确限速，偏差 <10%
- [ ] QoS 优先级队列：严格优先级，高优先级帧始终先于低优先级帧发送
- [ ] UDP 映射默认优先级高于 TCP 映射（UDP=2, TCP=4）
- [ ] 控制帧（Open/Close/Error）不受限速/队列影响，始终优先发送
- [ ] 不限速时（默认）零额外开销
- [ ] `dotnet test` 全部通过（含新增的 token bucket + QoS 单元测试）
- [ ] `dotnet build -c Release` 无警告
- [ ] Native AOT 发布成功
- [ ] 不引入 per-packet 堆分配

## Out of Scope

- **流量整形（Traffic Shaping）**：仅简单令牌桶丢弃/等待，不做 TBF 复杂整形
- **接收方向限速**：IntranetPeer 到 ExtranetPeer 的返回方向
- **跨 stream 公平性调度**：优先级仅在 session 内生效
- **动态调整速率**：不支持运行时修改限速参数
- **IntranetPeer 端限速**：只在 ExtranetPeer 发送方向限速

## Resolved Decisions

- **D1: 令牌桶插入在 PeerTransportSession.SendAsync 入口** — 在调用 KcpSession.Send / RelayClient.SendAsync 之前检查令牌桶。不在 KcpSession 内部限速（KCP 窗口控制不应绕过）。
- **D2: QoS 优先级通过多队列实现** — 每个优先级一个 `ConcurrentQueue<(byte[], int, int)>`，调度器按严格优先级取出帧发送。
- **D3: 限速+QoS 层作为可选包装** — 当 `RateLimitBytesPerSec == 0` 且所有映射优先级为默认值时，完全不创建队列/定时器对象，保持与现版本相同的零间接调用。
- **D4: UDP UnreliableData 也要纳入 QoS** — 虽然 UDP 走原始 socket，但数据仍经过 `ConnectionNegotiator.SendUnreliableAsync` → `UdpHolePuncher.Client.SendAsync`，在此入口插入 QoS 判别。

## Open Questions

_(None remaining — all product decisions resolved)_
