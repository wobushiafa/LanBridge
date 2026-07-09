# Design: P2P/KCP 数据通路集成测试

## Architecture

复用 `src/LanBridge.Tests/Integration/` 的 `EphemeralPortHelper`。新增 `KcpLoopbackPair` 辅助 + 测试类。

```
KcpLoopbackPair (helper)
  ├── udpA: UdpClient (loopback, ephemeral port)
  ├── udpB: UdpClient (loopback, ephemeral port)
  ├── sessionA: KcpSession(conv, udpA, epB, ownReceiveLoop:true)
  └── sessionB: KcpSession(conv, udpB, epA, ownReceiveLoop:true)
```

## Component Design

### 1. KcpLoopbackPair (helper, IDisposable)

```csharp
public sealed class KcpLoopbackPair : IDisposable
{
    public KcpSession SessionA { get; }
    public KcpSession SessionB { get; }

    public KcpLoopbackPair(uint? conv = null, bool verbose = false)
    {
        var convVal = conv ?? (uint)Random.Shared.Next(1, int.MaxValue);
        var udpA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var udpB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var epA = (IPEndPoint)udpA.Client.LocalEndPoint!;
        var epB = (IPEndPoint)udpB.Client.LocalEndPoint!;
        SessionA = new KcpSession(convVal, udpA, epB, ownReceiveLoop: true);
        SessionB = new KcpSession(convVal, udpB, epA, ownReceiveLoop: true);
        // udpA/udpB 被 KcpSession 接管（ownReceiveLoop:true 时 session 的 receive loop 用它）
        // dispose 由 KcpSession 负责? 核对——KcpSession.Dispose 是否 dispose UdpClient。
        // 若否，本类需跟踪 udpA/udpB 自己 dispose。
    }

    public void Start() { SessionA.Start(); SessionB.Start(); }
    public void Dispose() { SessionA.Dispose(); SessionB.Dispose(); }
}
```

**核对点**：`KcpSession.Dispose` 是否 dispose `_udpClient`？若不，`KcpLoopbackPair` 需自管 UdpClient 生命周期（持引用 + 自己 dispose）。implement 时读 `KcpSession.Dispose` 确认。

### 2. TestClient-like WaitFor 辅助
复用 `TestClient.WaitForAsync<T>` 的模式：`TaskCompletionSource` + 超时。但 KCP 数据是 `byte[]`+`int`（非 BaseMessage）。新增一个简单的 `ByteWaiter`：
```csharp
var tcs = new TaskCompletionSource<byte[]>();
sessionB.OnDataReceived += (data, len) => tcs.TrySetResult(data[..len]);
// send, await tcs.Task.WaitAsync(timeout), assert
```

### 3. 各测试

**REQ-1 `Kcp_Loopback_RoundtripsData`**：
```
using var pair = new KcpLoopbackPair();
var received = new TaskCompletionSource<byte[]>();
pair.SessionB.OnDataReceived += (data, len) => received.TrySetResult(data[..len]);
pair.Start();
var payload = Encoding.UTF8.GetBytes("hello kcp");
pair.SessionA.Send(payload, 0, payload.Length);
var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
Assert.Equal(payload, got);
```
反向同理（B→A）。

**REQ-2 `PeerTransportSession_RoutesDataThroughKcp`**：
```
using var pair = new KcpLoopbackPair();
using var peerA = new PeerTransportSession(false);
using var peerB = new PeerTransportSession(false);
peerA.UseP2p(pair.SessionA);
peerB.UseP2p(pair.SessionB);
var received = new TaskCompletionSource<byte[]>();
peerB.OnDataReceived += (data, len) => received.TrySetResult(data[..len]);
pair.Start();
var payload = new byte[500];
await peerA.SendAsync(payload, 0, payload.Length);
var got = await received.Task.WaitAsync(5s);
Assert.Equal(payload.Length, got.Length);
```
注意：`PeerTransportSession` 有 ping/pong 心跳逻辑（`HandleP2pData` 里处理 Ping/Pong）——发的 payload 不是 TunnelFrame 会被当 Ping/Pong，会走 `OnDataReceived?.Invoke(data, length)`。核对 `HandleP2pData`：只对 `frame.StreamId == 0 && Ping/Pong` 特殊处理，其余 `OnDataReceived`。普通 payload 应正常到达。**核对点**：`PeerTransportSession.HandleP2pData` 是否要求输入是合法 TunnelFrame？若是，裸 byte[] 可能被 `TryDecode` 失败丢弃。implement 时核对——可能需要发 TunnelFrame-encoded payload。

**REQ-3 `P2pPath_RateLimitThrottles`**：
```
peerA.SetRateLimit(new TokenBucket(100_000)); // 100 KB/s
pair.Start();
var data = new byte[1_000_000];
var sw = Stopwatch.StartNew();
for (offset in 1KB frames) await peerA.SendAsync(data, offset, 1000);
sw.Stop();
Assert.InRange(sw.Elapsed.TotalSeconds, 8.0, 15.0);
```

**REQ-4 `Kcp_FragmentsAndReassemblesLargeMessage`**：
发 100KB payload，断言对端收到完整 100KB 且内容一致（`SequenceEqual`）。KCP MTU=1200 → ~84 分片。

## 关键风险与核对点

1. **`PeerTransportSession.HandleP2pData` 对非 TunnelFrame 的处理**——若它要求合法 TunnelFrame，REQ-2/3 需发 `TunnelFrame.Data` 编码的 payload。核对后定。
2. **`KcpSession.Dispose` 是否 dispose UdpClient**——决定 `KcpLoopbackPair` 是否自管 UdpClient。
3. **KCP 握手时间**——`Send` 后数据非即时到达（需 3 次握手 + KCP tick）。`WaitForAsync(5s)` 足够。若 flaky，加大超时或先 `Thread.Sleep(100)` 等握手。
4. **`ownReceiveLoop`**——true 时 session 自己跑 receive loop 读 UdpClient。两个 session 各自跑，不冲突。

## Compatibility

- 纯测试新增，零产品代码改动。
- 复用 `EphemeralPortHelper`。
- 不影响现有 64 测试。

## Rollback

新测试文件，删掉即回滚。
