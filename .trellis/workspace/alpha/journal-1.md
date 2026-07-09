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
