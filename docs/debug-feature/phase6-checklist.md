# Phase 6 — WebSocket Transport

**Parent:** `docs/debug-feature/requirements.md` §11.7
**Depends on:** Phase 2 (ISink interface), Phase 0 (debug probe)
**Unlocks:** Visual debugger FE integration

---

## Acceptance Criteria

### WebSocket Server

- [ ] WebSocket server hosted via ASP.NET Core middleware (`UseWebSockets()`)
- [ ] Endpoint: `ws://host/api/debug-sessions/{id}/ws` (or similar)
- [ ] One WebSocket connection per observed run — connection scoped to a session
- [ ] Connection lifecycle: connect → receive events + send commands → disconnect
- [ ] Server handles connection drops gracefully (no crash, session continues)

### WebSocketSink

- [ ] Implements `ISink` interface from Phase 1
- [ ] Pushes serialized events to connected client in real-time
- [ ] Same JSON format as JSONL file sink — no separate serialization
- [ ] If no client is connected, sink is a no-op (events still go to JSONL file sink)
- [ ] Backpressure: if client is slow, events are dropped (not buffered indefinitely)
- [ ] Sink registered alongside `JsonlFileSink` in the event bus fan-out

### Control Commands via WebSocket

- [ ] Client sends control commands as JSON messages over WebSocket
- [ ] Same command set as Phase 0 HTTP POC:
  - `continue`, `next`, `next_bar`, `next_trade`, `run_to_sequence`, `run_to_timestamp`, `pause`
- [ ] Command messages parsed and dispatched to `GatingDebugProbe.SendCommandAsync()`
- [ ] Response (DebugSnapshot) sent back over WebSocket after break condition is met
- [ ] Error responses for invalid commands or commands when already pending

### Execution Modes

- [ ] **Visual debug mode**: WebSocket server active, engine starts paused, waits for client connection + command
- [ ] **No-client mode**: WebSocket server not started (or sink is no-op) — engine runs at full speed without pausing
- [ ] Mode determined by run configuration (e.g. `DebugMode.Visual` vs `DebugMode.Headless` vs `DebugMode.None`)

### Pause Semantics (§5.3)

- [ ] When paused: current event fully processed, all state in memory, waits for next command
- [ ] `continue` → engine runs at full speed, still emits events to all sinks
- [ ] Client can send `pause` after `continue` — takes effect at next bar boundary

### HTTP POC Deprecation

- [ ] Phase 0 HTTP REST endpoints (`/api/debug-sessions/`) marked as deprecated or removed
- [ ] All debug session management migrated to WebSocket-based flow
- [ ] If HTTP endpoints retained for session creation/deletion, they are clearly separated from event/command transport

### Tests

- [ ] Unit test: WebSocketSink sends serialized event over mock WebSocket
- [ ] Unit test: WebSocketSink is no-op when no client connected
- [ ] Unit test: WebSocketSink handles client disconnect without throwing
- [ ] Unit test: command parsing from WebSocket message produces correct `DebugCommand`
- [ ] Integration test: connect WebSocket → start session → send `next_bar` → receive events + snapshot → send `continue` → receive remaining events → session completes
- [ ] Integration test: two concurrent sessions on separate WebSocket connections don't interfere
- [ ] All existing tests pass unchanged

### Non-Functional

- [ ] No new NuGet packages (`System.Net.WebSockets` is in-box)
- [ ] WebSocket message size bounded (individual events are small — typically < 1KB)
- [ ] Latency: event push to client < 1ms after emission (local WebSocket)
