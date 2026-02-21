# Phase 6 — WebSocket Transport

**Parent:** `docs/debug-feature/requirements.md` §11.7
**Depends on:** Phase 2 (ISink interface), Phase 0 (debug probe)
**Unlocks:** Visual debugger FE integration

---

## Acceptance Criteria

### WebSocket Server

- [x] WebSocket server hosted via ASP.NET Core middleware (`UseWebSockets()`)
- [x] Endpoint: `ws://host/api/debug-sessions/{id}/ws` (or similar)
- [x] One WebSocket connection per observed run — connection scoped to a session
- [x] Connection lifecycle: connect → receive events + send commands → disconnect
- [x] Server handles connection drops gracefully (no crash, session continues)

### WebSocketSink

- [x] Implements `ISink` interface from Phase 1
- [x] Pushes serialized events to connected client in real-time
- [x] Same JSON format as JSONL file sink — no separate serialization
- [x] If no client is connected, sink is a no-op (events still go to JSONL file sink)
- [x] Backpressure: if client is slow, events are dropped (not buffered indefinitely)
- [x] Sink registered alongside `JsonlFileSink` in the event bus fan-out

### Control Commands via WebSocket

- [x] Client sends control commands as JSON messages over WebSocket
- [x] Same command set as Phase 0 HTTP POC:
  - `continue`, `next`, `next_bar`, `next_trade`, `run_to_sequence`, `run_to_timestamp`, `pause`
- [x] Command messages parsed and dispatched to `GatingDebugProbe.SendCommandAsync()`
- [x] Response (DebugSnapshot) sent back over WebSocket after break condition is met
- [x] Error responses for invalid commands or commands when already pending

### Execution Modes

- [x] **Visual debug mode**: WebSocket server active, engine starts paused, waits for client connection + command
- [x] **No-client mode**: WebSocket server not started (or sink is no-op) — engine runs at full speed without pausing
- [x] Mode determined by run configuration (e.g. `DebugMode.Visual` vs `DebugMode.Headless` vs `DebugMode.None`)

### Pause Semantics (§5.3)

- [x] When paused: current event fully processed, all state in memory, waits for next command
- [x] `continue` → engine runs at full speed, still emits events to all sinks
- [x] Client can send `pause` after `continue` — takes effect at next bar boundary

### HTTP POC Deprecation

- [x] Phase 0 HTTP REST endpoints (`/api/debug-sessions/`) marked as deprecated or removed
- [x] All debug session management migrated to WebSocket-based flow
- [x] If HTTP endpoints retained for session creation/deletion, they are clearly separated from event/command transport

### Tests

- [x] Unit test: WebSocketSink sends serialized event over mock WebSocket
- [x] Unit test: WebSocketSink is no-op when no client connected
- [x] Unit test: WebSocketSink handles client disconnect without throwing
- [x] Unit test: command parsing from WebSocket message produces correct `DebugCommand`
- [x] Integration test: connect WebSocket → start session → send `next_bar` → receive events + snapshot → send `continue` → receive remaining events → session completes
- [x] Integration test: two concurrent sessions on separate WebSocket connections don't interfere
- [x] All existing tests pass unchanged

### Non-Functional

- [x] No new NuGet packages (`System.Net.WebSockets` is in-box)
- [x] WebSocket message size bounded (individual events are small — typically < 1KB)
- [x] Latency: event push to client < 1ms after emission (local WebSocket)
