# Implementation Plan: TUI 可观测性增强

## Ordered Checklist

### Step 1: NegotiatorStats 扩展 + 填充
- [ ] `PeerSessionStats.cs`：`NegotiatorStats` 加 `RttMs`/`Cwnd`/`WaitSnd`/`SentBytes`/`ReceivedBytes`/`InputErrors`。
- [ ] `ConnectionNegotiator.GetStatsSnapshot()`：从 `_sessions.GetStatsSnapshot()`（PeerSessionStats）映射新字段。
- [ ] 核对所有 `new NegotiatorStats(...)` 构造点（grep）——确保都更新。
- [ ] 编译通过。

### Step 2: TuiDashboard 多隧道 + KCP + 吞吐率
- [ ] ctor 改 `Func<IReadOnlyList<NegotiatorStats>>`。
- [ ] 加吞吐率跟踪字段（`_lastSnapshotUtc`/`_lastSentBytes`/`_lastRecvBytes`）。
- [ ] Render：多隧道列表（每隧道一行：Target/Mode/RTT/cwnd/↑rate/↓rate/sent/recv）+ 总计吞吐 + KCP 健康行。
- [ ] 人类可读 bytes/sec 格式化辅助。
- [ ] DISC 模式 RTT/cwnd 显 `—`。

### Step 3: ExtranetPeer 接线 + 修空 telemetry
- [ ] `:275-286`：`firstNegotiator` 单个 → `_router.Negotiators.Values.Select(n => n.GetStatsSnapshot()).ToList()` 列表。
- [ ] telemetry 回调 `() => new Dictionary<>()` → 真实 `OperationalTelemetry` 快照（加 `GetSnapshot()` 返回 dict 若不存在，或用已有）。

### Step 4: IntranetPeer TUI（stretch）
- [ ] `--tui` flag 解析（`TransportOptions.EnableTui` 已有）。
- [ ] `TuiDashboard` 接线，列表单元素。
- [ ] help 文本加 `--tui`。

### Step 5: 验证
- [ ] `dotnet build LanBridge.slnx -c Release` 0 警告。
- [ ] `dotnet test` 全绿（69，无回归——TUI 无测试，但 stats record 变化不能破坏 `RateLimitIntegrationTests`/`Negotiator_WiresRateLimitFromConfig`）。
- [ ] 手动核对 TUI 渲染（若可行）——至少编译 + 不崩。

## Validation Commands
```bash
dotnet build LanBridge.slnx -c Release
dotnet test LanBridge.slnx -c Release
```

## Review Gates
- Gate 1（Step 1 后）：stats 扩展编译 + 现有测试绿。
- Gate 2（Step 5）：全绿，TUI 不崩。

## Rollback Points
- 若 NegotiatorStats 位置参数破坏某构造点：grep 全部 `new NegotiatorStats` 修。
- 若 OperationalTelemetry 无 dict 快照：TUI telemetry 区用 `FormatSnapshot()` 字符串显示（降级）。

## Notes
- TUI 无自动化测试（展示层）；靠编译 + 不崩 + 手动核对。
- 吞吐率在 TUI 内算（不污染 stats record）。
- IntranetPeer TUI 是 stretch，可跳过。
