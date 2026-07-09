# Implementation Plan: P2P/KCP 数据通路集成测试

## Ordered Checklist

### Step 1: KcpLoopbackPair 辅助
- [ ] `src/LanBridge.Tests/Integration/KcpLoopbackPair.cs`：`IDisposable`。建两个 `UdpClient`(loopback, port 0)，取各自 LocalEndPoint，建两个 `KcpSession`(同 conv, 互指, ownReceiveLoop:true)。
- [ ] **自管 UdpClient 生命周期**：`KcpSession.Dispose` 不 dispose UdpClient，本类持引用 + Dispose 里 dispose 两个 UdpClient + 两个 session。
- [ ] `Start()` 调两个 session.Start()。
- [ ] 复用 `EphemeralPortHelper` 或直接 `new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))`。

### Step 2: REQ-1 KCP 双向往返
- [ ] `KcpDataPathTests.cs`：`Kcp_Loopback_RoundtripsData`——A.Send(payload)→等 B.OnDataReceived，断言内容一致；反向 B→A。
- [ ] 用 `TaskCompletionSource<byte[]>` + `WaitAsync(5s)` 等接收。
- [ ] `[Fact(Timeout=30000)]`。

### Step 3: REQ-2 PeerTransportSession+Kcp
- [ ] `PeerTransport_RoutesDataThroughKcp`：两个 PeerTransportSession，`UseP2p(sessionA/B)`，`peerA.SendAsync(payload)`→等 `peerB.OnDataReceived`。
- [ ] 裸 byte[] payload（不需 TunnelFrame 编码——TryDecode 失败会 fall through 到 OnDataReceived）。
- [ ] 注意：PeerTransportSession 的 P2pMonitor 每 5s 发 Ping，但数据先到（TaskCompletionSource 抓首个 OnDataReceived）。

### Step 4: REQ-3 P2P 路径限速
- [ ] `P2pPath_RateLimitThrottles`：`peerA.SetRateLimit(new TokenBucket(100_000))`，发 1MB（1KB 帧），断言耗时 ∈ [8s,15s]。
- [ ] 与 `RateLimitIntegrationTests` 区别：这里走真实 KCP 路径（不只空转 SendCoreAsync）。

### Step 5: REQ-4 大消息分片
- [ ] `Kcp_FragmentsAndReassemblesLargeMessage`：发 100KB payload，断言对端收到完整 100KB + `SequenceEqual` 内容一致。
- [ ] KCP MTU=1200 → ~84 分片，验证重组。

### Step 6: 验证
- [ ] `dotnet build -c Release` 0 警告。
- [ ] `dotnet test` 全绿（64 + 新增，两轮无 flakiness）。
- [ ] 确认 KCP 握手时间不导致 flaky（5s 超时足够；若 flaky，加大或等 IsConnected）。

## Validation Commands
```bash
dotnet build LanBridge.slnx -c Release
dotnet test LanBridge.slnx -c Release
```

## Review Gates
- Gate 1（Step 1 后）：`KcpLoopbackPair` 编译通过。
- Gate 2（Step 6）：全绿，无 flakiness。

## Rollback Points
- 若 KCP 握手 flaky：在 Send 前 `await Task.Delay(200)` 等握手，或轮询 `IsConnected`。
- 若 REQ-2 的 PeerTransportSession 心跳干扰：用更大 payload 或抓特定长度。

## Notes
- 纯测试新增，零产品代码改动。
- 不测真实打洞握手（直接构造 KcpSession 跳过）。
- 不测丢包/拥塞（只测正常路径）。
