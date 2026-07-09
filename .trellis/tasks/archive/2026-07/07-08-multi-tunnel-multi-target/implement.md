# Implementation Plan: 多隧道多目标

## Pre-flight Checks

```bash
dotnet build LanBridge.slnx -c Release
dotnet test src/LanBridge.Tests/LanBridge.Tests.csproj
```

## Implementation Checklist

### Phase 1: 基础设施提取 (低风险，纯重构)

- [ ] **1.1** 创建 `SharedUdpStack` 类
  - 从 `ConnectionNegotiator` 中提取 `UdpHolePuncher` 创建、NAT 检测、LAN discovery 启动逻辑
  - 文件: `src/LanBridge.Common/Network/SharedUdpStack.cs` (新建)
  - 验证: `dotnet build` 通过

- [ ] **1.2** 创建 `SharedSignalingStack` 类
  - 从 `ConnectionNegotiator` 中提取 `SignalingConnectionLoop` 创建和 `HandleSignalingConnectedAsync` 逻辑
  - 文件: `src/LanBridge.Common/Network/SharedSignalingStack.cs` (新建)
  - 验证: `dotnet build` 通过

- [ ] **1.3** 创建 `SignalingMessageDispatcher` 类
  - 替代 `SignalingMessageRouter`，增加 sessionId/nodeId 路由能力
  - 文件: `src/LanBridge.Common/Network/SignalingMessageDispatcher.cs` (新建)
  - 验证: `dotnet build` 通过

- [ ] **1.4** 改造 `ConnectionNegotiator` 为注入式构造
  - 新构造函数: `ConnectionNegotiator(options, SharedUdpStack, SharedSignalingStack)`
  - 保留旧构造函数作为向后兼容包装（内部创建共享栈 — IntranetPeer 使用）
  - 移除内部创建 `UdpHolePuncher` / `SignalingConnectionLoop` / `LanDiscoveryService` 的代码
  - 改为使用注入的共享栈
  - 文件: `src/LanBridge.Common/Network/ConnectionNegotiator.cs`
  - 验证: `dotnet build` + `dotnet test` 通过，IntranetPeer 行为不变

### Phase 2: UdpHolePuncher 多远端改造 (核心改动)

- [ ] **2.1** `_remoteEndPoint` → `ConcurrentDictionary<string, IPEndPoint> _remoteEndPoints`
  - key 为 nodeId
  - 文件: `src/LanBridge.Common/Network/UdpHolePuncher.cs`
  - 验证: `dotnet build` 通过

- [ ] **2.2** 修改 `OnHolePunched` 事件签名，增加 `string nodeId` 参数
  - 从 `_pendingPunches` 中查找对应的 PendingPunch（已有 nodeId 关联），在 MarkPunched 时传递
  - 文件: `src/LanBridge.Common/Network/UdpHolePuncher.cs`
  - 验证: `dotnet build` 通过

- [ ] **2.3** 修改 `StartPunchingAsync` 支持并发多远端打洞
  - 每次调用仍然针对一个远端，但多次调用可并发执行
  - PUNCH 包发送不再写入全局 `_remoteEndPoint`，而是通过参数或 `_remoteEndPoints` 查询
  - 文件: `src/LanBridge.Common/Network/UdpHolePuncher.cs`
  - 验证: `dotnet build` + `dotnet test` 通过

- [ ] **2.4** 修改 `TryHandlePunchAsync` 和 `TriggerHolePunched` 以支持多远端
  - PUNCH 收到时从 RemoteEndPoint 解析 nodeId
  - 文件: `src/LanBridge.Common/Network/UdpHolePuncher.cs`
  - 验证: `dotnet build` + `dotnet test` 通过

### Phase 3: 配置层扩展

- [ ] **3.1** `TunnelMapping` 增加 `TargetNodeId` 属性
  - 文件: `src/LanBridge.ExtranetPeer/ExtranetPeer.cs`
  - 验证: `dotnet build` 通过

- [ ] **3.2** `TargetDescriptor` / `TargetDescriptorParser` 支持 `@nodeId` 语法
  - 格式: `host:port[:protocol][@nodeId]`
  - 文件: `src/LanBridge.Common/Protocol/TargetDescriptor.cs`
  - 验证: 新增单元测试覆盖 `@nodeId` 解析

- [ ] **3.3** `ExtranetPeer.Program.TryParseMapping` 支持 `@nodeId`
  - `-m 8554=192.168.7.230:554:tcp@intranet-peer-001`
  - 文件: `src/LanBridge.ExtranetPeer/Program.cs`
  - 验证: `dotnet build` 通过

- [ ] **3.4** JSON 配置 schema 文档更新
  - 文件: `README.md`
  - 验证: 无构建错误

### Phase 4: TunnelRouter + 多 Negotiator 编排

- [ ] **4.1** 创建 `TunnelRouter` 类
  - 管理 `mapping → nodeId` 路由表
  - 管理 `nodeId → ConnectionNegotiator` 字典
  - 为每个唯一 nodeId 创建 ConnectionNegotiator 实例
  - 文件: `src/LanBridge.Common/Network/TunnelRouter.cs` (新建)
  - 验证: `dotnet build` 通过

- [ ] **4.2** 改造 `ExtranetPeer` 使用 TunnelRouter
  - 替换单 `_connection` 字段为 `TunnelRouter`
  - `HandleTunnelData` 按 sessionId/StreamId 路由到本地客户端
  - `AcceptLocalClientsAsync` 按 mapping 路由到对应 Negotiator
  - 文件: `src/LanBridge.ExtranetPeer/ExtranetPeer.cs`
  - 验证: `dotnet build` + `dotnet test` 通过

- [ ] **4.3** ExtranetPeer 启动流程改造
  - 创建 SharedUdpStack / SharedSignalingStack
  - 从 config.Mappings 提取唯一 nodeId 列表
  - 为每个 nodeId 创建 ConnectionNegotiator 并注入共享栈
  - 文件: `src/LanBridge.ExtranetPeer/ExtranetPeer.cs`
  - 验证: `dotnet build` 通过

### Phase 5: 端到端验证

- [ ] **5.1** 单目标回归测试
  - 使用现有配置（无 `@nodeId`），验证行为与改造前一致
  - `dotnet test` + `dotnet run --project src/LanBridge.KcpTest -- --smoke-only`

- [ ] **5.2** 多目标配置测试
  - 手动测试：1 个 ExtranetPeer 连接 2 个 IntranetPeer，每个 mapping 指向不同 nodeId
  - 验证数据路由正确、单隧道断开不影响其他

- [ ] **5.3** Release 构建验证
  - `dotnet build LanBridge.slnx -c Release`
  - Native AOT publish 验证（至少 linux-x64）

## Risky Files

| 文件 | 风险 | 理由 |
|------|------|------|
| `ConnectionNegotiator.cs` | 高 | 核心重构，注入式改造影响所有调用方 |
| `UdpHolePuncher.cs` | 高 | 多远端改造，打洞状态管理变复杂 |
| `ExtranetPeer.cs` | 中 | 从单 Negotiator 改为多，数据路由重写 |
| `TargetDescriptor.cs` | 低 | 新增 `@nodeId` 解析，向后兼容 |

## Rollback Points

- Phase 1 完成后：ConnectionNegotiator 保留旧构造函数，可随时回退
- Phase 2 完成后：UdpHolePuncher 多远端在单目标下等价于原行为
- Phase 4 完成后：无 `@nodeId` 配置下只创建一个 Negotiator，完全等价原行为
