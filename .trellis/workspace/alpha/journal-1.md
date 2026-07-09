# Journal - alpha (Part 1)

> AI development session journal
> Started: 2026-07-08

---



## Session 1: Wire WebSocket signaling end-to-end

**Date**: 2026-07-08
**Task**: Wire WebSocket signaling end-to-end
**Branch**: `main`

### Summary

Completed the WebSocket signaling transport channel: wired --ws-port and --signaling-transport tcp|ws|auto CLI + config end-to-end (server startup, PeerConnectionOptions/SharedSignalingStack/TunnelRouter threading), and fixed two blockers the prior core-class commit left -- WS connected callback was skipped (peers never registered over WS) and token-reject called TcpClient.Dispose on a null WS client (NullRef). Added transport-agnostic outbound routing (sender bridge registry keyed by clientId, stale-node cleanup, DisconnectClientAsync). 13 files +188/-23; build 0 warnings; 56/56 tests pass. Captured contract in .trellis/spec/backend/signaling-transport.md.

### Main Changes

(Add details)

### Git Commits

| Hash | Message |
|------|---------|
| `25011d5` | (see git log) |

### Testing

- [OK] (Add test results)

### Status

[OK] **Completed**

### Next Steps

- None - task complete


## Session 2: Wire bandwidth rate-limit and QoS into runtime

**Date**: 2026-07-09
**Task**: Wire bandwidth rate-limit and QoS into runtime
**Branch**: `main`

### Summary

Wired the already-implemented TokenBucket/PriorityFrameQueue/PeerTransportSession QoS core into the runtime (prior commit left SetRateLimit/SetPriority never called). PeerConnectionOptions carries rate/priority; GetSession applies them; SendUnreliableAsync applies the bucket before raw UDP (D4); ExtranetPeer aggregates per-node QoS (most-restrictive rate, highest priority) into TunnelRouter; CLI --rate-limit/--priority; TUI shows rate stats. Check agent caught a real pending-local leak bug (self-fixed) + flagged MEDIUM control-frame priority gap (committed core logic, follow-up). 8 files +128/-6; build 0 warnings; 56/56 tests; AOT publish OK. Spec captured in bandwidth-qos.md.

### Main Changes

(Add details)

### Git Commits

| Hash | Message |
|------|---------|
| `53021fd` | (see git log) |

### Testing

- [OK] (Add test results)

### Status

[OK] **Completed**

### Next Steps

- None - task complete


## Session 3: Add in-process integration test harness

**Date**: 2026-07-09
**Task**: Add in-process integration test harness
**Branch**: `main`

### Summary

Built in-process integration test harness (SignalingTestCluster + TestClient) covering TCP/WS register roundtrip, auto fallback, coexistence, ConnectRequest/RelayRequest mediation, and rate-limit effective+ wiring. 8 new tests (64 total). Minimal product enablers: SignalingService.ActualPort + WebSocketSignalingService.bindAllNics param (defaults preserve production). Harness self-validated: caught a deliberately-injected && false unwire of SetRateLimit that the implement agent left in (removed). check sub-agent output was corrupted; I verified inline — 2 stable runs, no flakiness, no admin needed. localhost host-matching caveat for WS noted.

### Main Changes

(Add details)

### Git Commits

| Hash | Message |
|------|---------|
| `d531389` | (see git log) |

### Testing

- [OK] (Add test results)

### Status

[OK] **Completed**

### Next Steps

- None - task complete


## Session 4: ConnectionNegotiator split phase 1: extract data plane

**Date**: 2026-07-09
**Task**: ConnectionNegotiator split phase 1: extract data plane
**Branch**: `main`

### Summary

Phase 1 of splitting the ConnectionNegotiator god class: extracted the data plane (session storage, GetSession factory with QoS wiring + events, reliable/high-priority send, session state, stats) into new PeerSessionManager (100 lines). ConnectionNegotiator (872->853 lines) now delegates; public API 100% unchanged, zero caller impact. One design deviation: GetStatsSnapshot stays on the facade (NegotiatorStats mixes data+control plane). SendUnreliableAsync stays (uses hole puncher, later phase). 64/64 tests pass incl integration suite (QoS wiring survived the move). Check sub-agent had corrupted last time so verified inline.

### Main Changes

(Add details)

### Git Commits

| Hash | Message |
|------|---------|
| `3a07bb2` | (see git log) |

### Testing

- [OK] (Add test results)

### Status

[OK] **Completed**

### Next Steps

- None - task complete


## Session 5: ConnectionNegotiator split phase 2: extract NAT/punch subsystem

**Date**: 2026-07-09
**Task**: ConnectionNegotiator split phase 2: extract NAT/punch subsystem
**Branch**: `main`

### Summary

Phase 2: extracted NAT/hole-punch subsystem into PeerPunchCoordinator (468 lines). ConnectionNegotiator 853->538 lines (-37%). Decoupled via 3 events OUT + 8 callback delegates IN + PeerSessionManager (no negotiator ref -> no cycle). SendUnreliableAsync migrated (D4 preserved). _lanDiscovery stays in negotiator (LanDiscoveryService circular dep). 6 deviations from design, all mechanical. 64/64 tests pass two runs, zero caller impact. Verified inline (check sub-agent had corrupted previously).

### Main Changes

(Add details)

### Git Commits

| Hash | Message |
|------|---------|
| `9f9917b` | (see git log) |

### Testing

- [OK] (Add test results)

### Status

[OK] **Completed**

### Next Steps

- None - task complete


## Session 6: Add P2P/KCP data-path integration tests

**Date**: 2026-07-09
**Task**: Add P2P/KCP data-path integration tests
**Branch**: `main`

### Summary

Added 4 loopback integration tests for the KCP data path (previously zero coverage — highest-risk untested path). KcpLoopbackPair helper + KcpDataPathTests (REQ-1 KCP roundtrip, REQ-2 PeerTransportSession+KCP, REQ-3 rate-limit on real P2P path, REQ-4 fragmentation/reassembly). 68/68 tests pass two runs. Zero product changes. Discovered product limitation: KcpSession.InputPacket 64KB buffer caps deliverable message size (REQ-4 used 60000 bytes). Double-dispose wrinkle handled via tolerant TryDisposeSession.

### Main Changes

(Add details)

### Git Commits

| Hash | Message |
|------|---------|
| `45d134b` | (see git log) |

### Testing

- [OK] (Add test results)

### Status

[OK] **Completed**

### Next Steps

- None - task complete


## Session 7: Unify SignalingService connection abstraction

**Date**: 2026-07-09
**Task**: Unify SignalingService connection abstraction
**Branch**: `main`

### Summary

Unified SignalingService dual-track connection mgmt (TCP raw objects + WS delegate bridges) into single ISignalingConnection abstraction: ISignalingConnection + TcpSignalingConnection + BridgeSignalingConnection. Single _connections registry, single-path SendToClientAsync, DisconnectClientAsync dropped TcpClient? param. RegisterConnection/UnregisterConnection + RemoveNodeBinding. WebSocketSignalingService 2 call sites updated. 2 deviations (DisconnectClientAsync doesn't double-count RemoveNodeBinding; dropped dead TcpClient param eliminating null! hack). SignalingService net -55 lines. 69/69 tests pass two runs, cross-transport integration green. Spec signaling-transport.md updated with new API.

### Main Changes

(Add details)

### Git Commits

| Hash | Message |
|------|---------|
| `0a87a34` | (see git log) |

### Testing

- [OK] (Add test results)

### Status

[OK] **Completed**

### Next Steps

- None - task complete


## Session 8: Enhance TUI dashboard observability

**Date**: 2026-07-10
**Task**: Enhance TUI dashboard observability
**Branch**: `main`

### Summary

TuiDashboard: NegotiatorStats +6 KCP fields (RTT/cwnd/WaitSnd/SentBytes/ReceivedBytes/InputErrors), ctor takes multi-tunnel list, per-tunnel table + KCP link stats + live throughput (delta/interval, human-readable) + real telemetry (peers had none—added OperationalTelemetry + state_* counter). ExtranetPeer passes ALL negotiators (was first-only); empty telemetry callback fixed. IntranetPeer gained --tui (was TUI-less). 6 files +217/-43. 69/69 tests pass, AOT publish clean.

### Main Changes

(Add details)

### Git Commits

| Hash | Message |
|------|---------|
| `0ff0284` | (see git log) |

### Testing

- [OK] (Add test results)

### Status

[OK] **Completed**

### Next Steps

- None - task complete
