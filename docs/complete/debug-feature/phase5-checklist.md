# Phase 5 — Post-Run Persistence (SQLite)

**Parent:** `docs/debug-feature/requirements.md` §11.6
**Depends on:** Phase 3 (JSONL event stream available)
**Unlocks:** AI debugger workflow, cross-run trade analysis

---

## Acceptance Criteria

### AI Debug Index (index.sqlite)

- [x] Built post-run by parsing the run's `events.jsonl`
- [x] Built **if enabled in backtest settings** — not built for optimization runs by default
- [x] Location: `Data/EventLogs/{run_folder}/index.sqlite` — co-located with JSONL
- [x] Schema: `events` table with columns:
  - `sq` (uint64, indexed) — sequence number
  - `ts` (text, indexed) — ISO 8601 timestamp
  - `_t` (text, indexed) — event type
  - `src` (text, indexed) — source component
  - `raw` (text) — full JSON event
- [x] All four index columns are queryable via standard SQL
- [x] Builds transactionally — partial index not left on disk if build fails

### Trade DB (trades.sqlite)

- [x] Built post-run **always**, for every execution mode (backtest, optimization, live)
- [x] Location: `Data/trades.sqlite` — single shared file across all runs
- [x] Schema:
  - `runs` table: run folder name, strategy, version, asset, period, full params (JSON), start/end time, mode, summary stats
  - `orders` table: FK to run, order ID, side, type, quantity, status, timestamps
  - `trades` table: FK to order, fill price, fill quantity, commission, timestamp
- [x] Inserts transactionally after run completes — one transaction per run
- [x] Concurrent optimization runs each insert their data on completion without blocking others (SQLite WAL mode or serialized access)

### Post-Run Pipeline

- [x] Pipeline triggered after `run.end` event (or after run task completes)
- [x] Order: JSONL → `index.sqlite` (if enabled) → `trades.sqlite` (always)
- [x] `meta.json` written before SQLite build starts
- [x] Pipeline errors logged but do not fail the run result (JSONL is the source of truth)

### Crash Recovery

- [x] If run terminates abnormally, JSONL is the only artifact
- [x] Utility command/method can rebuild `index.sqlite` from existing JSONL
- [x] Utility command/method can re-extract trade data from JSONL into `trades.sqlite`
- [x] Rebuild is idempotent — re-running on an already-built index replaces it

### AI Debugger Skill File (SKILL.md)

- [x] Skill file documents:
  - Event envelope schema and compact field names (`ts`, `sq`, `_t`, `src`, `d`)
  - All event types and their `d` payload shapes
  - File layout and naming conventions
  - Recommended `jq` patterns for JSONL querying
  - Recommended `sqlite3` query patterns for index and trade DB
  - Debugging workflow decision tree (triage → execution → indicator → comparison)
- [x] Skill file is self-contained — an AI agent can debug a run with only `SKILL.md` + the run artifacts
  > Moved to `.claude/skills/agent.debug.md`

### Tests

- [x] Unit test: index builder produces correct SQLite schema from sample JSONL
- [x] Unit test: index builder — query by `_t` returns correct events
- [x] Unit test: index builder — query by `sq` range returns ordered events
- [x] Unit test: trade DB builder extracts `ord.*` and `pos` events correctly
- [x] Unit test: trade DB — `runs` row contains correct metadata
- [x] Unit test: trade DB — `orders` and `trades` rows match JSONL event data
- [x] Unit test: concurrent inserts into shared `trades.sqlite` from two runs don't corrupt data
- [x] Unit test: crash recovery — rebuild from JSONL matches original index
- [x] Integration test: full backtest → verify index.sqlite queryable and trade DB populated
- [x] All existing tests pass unchanged

### Non-Functional

- [x] SQLite accessed via `Microsoft.Data.Sqlite` (already in .NET SDK) or lightweight wrapper — no heavy ORM
- [x] Index build time reasonable: < 5s for 100K events
- [x] Trade DB write does not hold lock longer than the insert transaction
