# WebSocket 信令传输通道

## Goal

为 SignalingServer 增加 WebSocket 信令传输选项，客户端可通过 WS/WSS 连接信令服务器，突破仅允许 HTTP 出站的防火墙限制。与现有 TCP 信令并存，客户端可配置传输方式。

## Background

当前信令系统使用原始 TCP + 4 字节长度前缀 + UTF-8 JSON 消息帧：

- `SignalingClient`：`TcpClient` → `NetworkStream`，读 4 字节长度 + N 字节 JSON
- `SignalingConnectionLoop`：管理重连，委托给 `SignalingClient`
- `SignalingService`（服务端）：`TcpListener` 接受客户端，相同的帧格式
- 消息使用 `System.Text.Json` Source Generators 序列化

某些企业/校园网络仅允许 HTTP/HTTPS 出站流量，阻断了 TCP 9000 直连。WebSocket 走 HTTP Upgrade，可被这些网络放行。

## Requirements

### REQ-1: 服务端 WebSocket 监听

SignalingServer 新增可选的 WebSocket 监听端口（默认 9010），使用 `HttpListener` 接受 WebSocket 升级请求。与 TCP 信令端口并存运行。

### REQ-2: 客户端 WebSocket 传输

新增 `WebSocketSignalingClient`，实现与 `SignalingClient` 相同的消息收发接口。使用 `ClientWebSocket` 连接 `ws://` 或 `wss://` 端点。

### REQ-3: 传输接口抽象

提取 `ISignalingTransport` 接口（或基类），`SignalingClient` 和 `WebSocketSignalingClient` 均实现该接口。`SignalingConnectionLoop` 依赖接口而非具体实现。

### REQ-4: 传输方式配置

- 服务端：`--ws-port 9010` 启用 WebSocket 监听（默认不启用）
- 客户端：`--signaling-transport tcp|ws|auto`（默认 tcp）
  - `auto`：先尝试 TCP，失败后回退 WS

### REQ-5: 帧格式兼容

WebSocket 帧使用文本帧（Text frame），内容为 JSON 字符串（与 TCP 模式的 JSON 内容完全一致，无需 4 字节长度前缀）。服务端消息分发逻辑不感知传输层差异。

### REQ-6: 向后兼容

- 不配置 `--ws-port` 时，服务端行为不变
- 不配置 `--signaling-transport` 时，客户端行为不变（纯 TCP）

## Acceptance Criteria

- [ ] 服务端 `--ws-port 9010` 后可通过 WebSocket 连接信令
- [ ] 客户端 `--signaling-transport ws` 可完成注册、打洞、中继全流程
- [ ] `--signaling-transport auto` 在 TCP 不通时自动回退 WS
- [ ] TCP 和 WS 客户端可同时连接同一信令服务器，互不影响
- [ ] 不启用 WS 时行为与当前完全一致
- [ ] `dotnet build -c Release` + `dotnet test` 通过
- [ ] 兼容 Native AOT

## Out of Scope

- TLS/WSS 证书管理（可用反向代理如 nginx 处理）
- WebSocket 认证/子协议协商
- HTTP API 端点（仅用于 WebSocket 升级）

## Resolved Decisions

- **D1: 服务端使用 HttpListener 而非 ASP.NET Core** — 保持轻量，与当前无框架的架构一致。HttpListener 支持 WebSocket 且兼容 Native AOT。
- **D2: WebSocket 使用文本帧 + JSON** — 与 TCP 模式的消息格式完全一致，服务端消息处理逻辑零修改。二进制帧预留给未来加密扩展。
- **D3: 客户端接口使用抽象基类而非接口** — 避免在 Native AOT 中接口虚调用的潜在问题，共享 `_sendLock` / `IsConnected` 等通用逻辑。

## Open Questions

_(None remaining)_
