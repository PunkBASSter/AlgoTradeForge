# Phase 2 — JSONL File Sink & Run Identity

**Parent:** `docs/debug-feature/requirements.md` §11.3
**Depends on:** Phase 1 (event model + bus)
**Unlocks:** Phase 3, Phase 6

---

## Acceptance Criteria

### Run Folder Naming & Directory Structure

- [x] Run folders created under `{AppDataRoot}/AlgoTradeForge/Data/EventLogs/`
- [x] Folder name format: `{strategy_name}_v{version}_{asset}_{period}_{params_hash}_{timestamp}`
  - `strategy_name` + `version` — human-readable identification
  - `asset` — primary traded instrument (e.g. `ETH-USD`)
  - `period` — backtest date range (e.g. `2024-2025`)
  - `params_hash` — short hash of full parameter set (for uniqueness)
  - `timestamp` — run start time ISO compact (e.g. `20260208T143201`)
- [x] Folder created atomically at run start (before first event)
- [x] Parallel runs produce separate directories (no shared files between runs)

### JsonlFileSink

- [x] Implements `ISink` interface from Phase 1
- [x] Writes to `events.jsonl` inside the run folder
- [x] Append-only: one JSON object per line, newline-terminated
- [x] Uses `FileStream` with `FileShare.Read` to allow concurrent reading (AI debugger, monitoring)
- [x] Flushes after each event (no buffering that could lose events on crash)
- [x] Events written in emission order (preserves monotonic `sq` ordering)

### meta.json

- [x] Written at `run.end` (not during run)
- [x] Contains: strategy name, version, asset, period, full parameter set (JSON), start time, end time, execution mode, summary stats
- [x] Schema matches what `RunEndEvent` carries
- [x] If run terminates abnormally (crash), `meta.json` may be absent — JSONL is the only guaranteed artifact

### Integration

- [x] Sink registered in DI and wired into `EventBus` at run setup time
- [x] Sink creation receives run folder path (determined by run identity)
- [x] Sink implements `IDisposable` / `IAsyncDisposable` — flushes and closes file handle on disposal

### Tests

- [x] Unit test: `JsonlFileSink` writes valid JSONL (each line parses as JSON)
- [x] Unit test: events appear in file in `sq` order
- [x] Unit test: concurrent `File.ReadAllLines` succeeds while sink is actively writing (FileShare.Read)
- [x] Unit test: run folder name generated correctly from strategy/asset/params inputs
- [x] Unit test: `params_hash` differs when parameters change, same when parameters identical
- [x] Unit test: `meta.json` written only after `run.end` event
- [x] Integration test: push events through `EventBus` → verify JSONL file content matches
- [x] All existing tests pass unchanged

### Non-Functional

- [x] No new NuGet packages (System.Text.Json for serialization already available)
- [x] JSONL write path does not allocate beyond the serialized string + newline
- [x] File handle released promptly on sink disposal
