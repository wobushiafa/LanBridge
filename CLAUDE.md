# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **LanBridge** (1019 symbols, 2984 relationships, 90 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

> If any GitNexus tool warns the index is stale, run `npx gitnexus analyze` in terminal first.

## Always Do

- **MUST run impact analysis before editing any symbol.** Before modifying a function, class, or method, run `gitnexus_impact({target: "symbolName", direction: "upstream"})` and report the blast radius (direct callers, affected processes, risk level) to the user.
- **MUST run `gitnexus_detect_changes()` before committing** to verify your changes only affect expected symbols and execution flows.
- **MUST warn the user** if impact analysis returns HIGH or CRITICAL risk before proceeding with edits.
- When exploring unfamiliar code, use `gitnexus_query({query: "concept"})` to find execution flows instead of grepping. It returns process-grouped results ranked by relevance.
- When you need full context on a specific symbol — callers, callees, which execution flows it participates in — use `gitnexus_context({name: "symbolName"})`.

## Never Do

- NEVER edit a function, class, or method without first running `gitnexus_impact` on it.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis.
- NEVER rename symbols with find-and-replace — use `gitnexus_rename` which understands the call graph.
- NEVER commit changes without running `gitnexus_detect_changes()` to check affected scope.

## Resources

| Resource | Use for |
|----------|---------|
| `gitnexus://repo/LanBridge/context` | Codebase overview, check index freshness |
| `gitnexus://repo/LanBridge/clusters` | All functional areas |
| `gitnexus://repo/LanBridge/processes` | All execution flows |
| `gitnexus://repo/LanBridge/process/{name}` | Step-by-step execution trace |

## CLI

| Task | Read this skill file |
|------|---------------------|
| Understand architecture / "How does X work?" | `.claude/skills/gitnexus/gitnexus-exploring/SKILL.md` |
| Blast radius / "What breaks if I change X?" | `.claude/skills/gitnexus/gitnexus-impact-analysis/SKILL.md` |
| Trace bugs / "Why is X failing?" | `.claude/skills/gitnexus/gitnexus-debugging/SKILL.md` |
| Rename / extract / split / refactor | `.claude/skills/gitnexus/gitnexus-refactoring/SKILL.md` |
| Tools, resources, schema reference | `.claude/skills/gitnexus/gitnexus-guide/SKILL.md` |
| Index, status, clean, wiki CLI commands | `.claude/skills/gitnexus/gitnexus-cli/SKILL.md` |

<!-- gitnexus:end -->

# Development

## Project Layout

- `src/LanBridge.Common/` — shared library: KCP implementation, STUN protocol, UDP hole punching, signaling/Relay clients, `TunnelFrame` protocol, LAN auto-discovery.
- `src/LanBridge.SignalingServer/` — public server executable: STUN service, TCP signaling, TCP relay fallback.
- `src/LanBridge.IntranetPeer/` — inside-network agent: registers with the signaling server, accepts tunnel requests, opens TCP/UDP connections to allowed LAN targets.
- `src/LanBridge.ExtranetPeer/` — outside-client executable: exposes local TCP/UDP proxy ports and forwards traffic through the tunnel to the IntranetPeer.
- `src/LanBridge.KcpTest/` — standalone console simulation for the custom KCP stack (not a unit-test project).
- `src/LanBridge.Tests/` — xUnit unit tests (BinaryHelper, MessageSerializer, TunnelFrame, configuration/CIDR, protocol, P2P conversion). Run with `dotnet test`.

## Requirements

- .NET 10 SDK
- Linux Native AOT builds additionally require `clang` and `zlib1g-dev` (installed in the Dockerfile build stage).

## Common Commands

```bash
# Build the solution (Debug)
dotnet build LanBridge.slnx

# Build Release
dotnet build LanBridge.slnx -c Release

# Run the signaling server
dotnet run --project src/LanBridge.SignalingServer

# Run with a config file
dotnet run --project src/LanBridge.SignalingServer -- -c server.config.json

# Run the intranet peer
dotnet run --project src/LanBridge.IntranetPeer -- \
  --signaling-host lanbridge.example.com \
  --stun-host lanbridge.example.com \
  --allow-subnet 192.168.7.0/24

# Run the extranet peer
dotnet run --project src/LanBridge.ExtranetPeer -- \
  --signaling-host lanbridge.example.com \
  --stun-host lanbridge.example.com \
  --target-node intranet-peer-001 \
  -m 8554=192.168.7.230:554:tcp \
  -m 9999=192.168.7.230:9999:udp

# Run the KCP simulation / smoke test (interactive console, not part of dotnet test)
dotnet run --project src/LanBridge.KcpTest

# Run the unit tests
dotnet test src/LanBridge.Tests/LanBridge.Tests.csproj

# Publish a Native AOT self-contained binary
dotnet publish src/LanBridge.SignalingServer/LanBridge.SignalingServer.csproj \
  -c Release -r linux-x64 --self-contained

# Build the signaling-server Docker image
docker build -f src/LanBridge.SignalingServer/Dockerfile -t lanbridge-signaling .
```

`src/LanBridge.Tests/` is the xUnit unit-test project (40 tests covering `BinaryHelper`, `MessageSerializer`, `TunnelFrame`, configuration/CIDR parsing, protocol conversion, and proxy control). Run it with `dotnet test`. `LanBridge.KcpTest` is a separate interactive console simulation for the KCP transport and LAN discovery logic, not part of the unit-test suite.

# High-Level Architecture

LanBridge is a P2P NAT-traversal tunnel. It exposes remote LAN services to an extranet client through three possible transports, chosen in order:

1. **LAN direct bypass** — when both peers are on the same local subnet, UDP multicast/broadcast on port `9005` discovers the peer directly and establishes a KCP tunnel without using the signaling server or STUN.
2. **P2P UDP direct** — otherwise, peers exchange public endpoints via the signaling server, perform UDP hole punching (with symmetric-NAT port prediction), and then run KCP over the punched UDP socket.
3. **TCP relay fallback** — if P2P fails and the extranet peer has `EnableRelayFallback` enabled, both peers open a second TCP connection to the signaling server’s relay port and traffic is bridged by the server.

The extranet peer binds local TCP/UDP listener ports defined by `--map` (or `Mappings`). Each accepted local client is assigned a `StreamId` and wrapped in `TunnelFrame` messages (16-byte header + payload). The IntranetPeer decodes each frame, opens a TCP or UDP connection to the requested `host:port[:protocol]`, and forwards data back and forth.

Key architectural components:

- `ConnectionNegotiator` (Common) — central state machine shared by both peers. Manages signaling-client reconnections, STUN/NAT detection, hole punching, LAN discovery, and selects `PeerTransportMode` (None → P2pDirect → Relay).
- `UdpHolePuncher` (Common) — owns the dual-stack UDP socket used for STUN, hole punching, KCP, unreliable UDP passthrough, and LAN discovery frames. Dispatches incoming UDP datagrams to STUN, KCP, unreliable data, or punch handlers.
- `PeerTransportSession` (Common) — wraps an active transport, either a `KcpSession` for P2P or a `RelayClient` for relay, and exposes `SendAsync`/`OnDataReceived`.
- `KcpSession` / `Kcp` (Common) — custom reliable-UDP implementation with fast retransmit, fast recovery, RTT/RTO estimation, and an optional BBR-style adaptive congestion control (`UseAdaptiveCongestion`).
- `TunnelFrame` (Common) — framing protocol used over the P2P or Relay path. Types include `Open`, `Data`, `UnreliableData`, `Close`, `Ping`, and `Pong`.
- `SignalingService` / `StunService` / `RelayService` (SignalingServer) — three services running in parallel; the relay service pairs intranet/extranet TCP clients by session ID.
- `IntranetPeer` / `ExtranetPeer` — host-specific entry points that create `ConnectionNegotiator`, start local proxies, and map `TunnelFrame` traffic to local TCP/UDP sockets.

The code is designed for Native AOT: all JSON serialization uses `System.Text.Json` source generators (`MessageJsonContext`, `ServerConfigJsonContext`, `IntranetConfigJsonContext`, `ExtranetConfigJsonContext`), and the three main executables have `PublishAot=true`. The hot data path uses `ArrayPool<byte>` and in-place header writes to avoid per-packet allocations.
