# AlgoTradeForge AI Debugger Skill

This document is a self-contained reference for AI agents debugging AlgoTradeForge backtest runs.
With this file and the run artifacts, an agent can fully investigate any run.

---

## 1. Run Artifacts File Layout

```
Data/
  EventLogs/
    {strategy}_v{version}_{asset}_{startYear}-{endYear}_{hash}_{yyyyMMddTHHmmss}/
      events.jsonl      # Source of truth — one JSON event per line
      meta.json         # Run identity + summary (written after run completes)
      index.sqlite      # SQL-queryable event index (built post-run)
  trades.sqlite         # Cross-run trade database (shared across all runs)
```

- `{hash}` = first 3 bytes of SHA256 of sorted strategy params JSON, as 6 lowercase hex chars. `"000000"` when no params.
- `meta.json` fields: `strategyName`, `strategyVersion`, `assetName`, `startTime`, `endTime`, `initialCash`, `runMode`, `runTimestamp`, `strategyParameters`, `totalBarsProcessed`, `finalEquity`, `totalFills`, `duration`.

---

## 2. Event Envelope Schema

Every line in `events.jsonl` is a JSON object with this envelope:

```json
{
  "ts":  "2024-01-01T00:01:00+00:00",   // ISO 8601 timestamp
  "sq":  42,                              // sequence number (monotonic int64)
  "_t":  "bar",                           // event type ID
  "src": "engine",                        // source component
  "d":   { ... }                          // event-specific payload
}
```

| Field | Type   | Description |
|-------|--------|-------------|
| `ts`  | string | ISO 8601 timestamp of when the event occurred |
| `sq`  | int64  | Monotonically increasing sequence number (starts at 1) |
| `_t`  | string | Event type identifier (see table below) |
| `src` | string | Source: `"engine"` or `"indicator.{name}"` |
| `d`   | object | Event-type-specific data payload |

---

## 3. Event Types and Payload Shapes

### System Events

| `_t` | Event | `d` payload |
|------|-------|-------------|
| `run.start` | Run begins | `{ strategyName, assetName, initialCash, startTime, endTime, runMode }` |
| `run.end` | Run completes | `{ totalBarsProcessed, finalEquity, totalFills, duration }` |
| `err` | Error | `{ message, stackTrace? }` |
| `warn` | Warning | `{ message }` |

### Market Events

| `_t` | Event | `d` payload |
|------|-------|-------------|
| `bar` | Bar completed | `{ assetName, timeFrame, open, high, low, close, volume }` |
| `bar.mut` | Bar mutated (intra-bar) | `{ assetName, timeFrame, open, high, low, close, volume }` |
| `tick` | Tick received | `{ assetName, price, quantity }` |

Price fields (`open`, `high`, `low`, `close`, `price`) are **Int64** (scaled by tick size).

### Order Events

| `_t` | Event | `d` payload |
|------|-------|-------------|
| `ord.place` | Order submitted | `{ orderId, assetName, side, type, quantity, limitPrice?, stopPrice? }` |
| `ord.fill` | Order filled | `{ orderId, assetName, side, price, quantity, commission }` |
| `ord.cancel` | Order cancelled | `{ orderId, assetName, reason? }` |
| `ord.reject` | Order rejected | `{ orderId, assetName, reason }` |
| `pos` | Position updated | `{ assetName, quantity, averageEntryPrice, realizedPnl }` |

- `side`: `"buy"` or `"sell"`
- `type`: `"market"`, `"limit"`, `"stop"`, `"stopLimit"`
- `price`, `commission`, `averageEntryPrice`, `realizedPnl` are **Int64**
- `quantity` is **decimal** (JSON number)

### Signal & Risk Events

| `_t` | Event | `d` payload |
|------|-------|-------------|
| `sig` | Strategy signal | `{ signalName, assetName, direction, strength, reason? }` |
| `risk` | Risk check | `{ assetName, passed, checkName, reason? }` |

### Indicator Events

| `_t` | Event | `d` payload |
|------|-------|-------------|
| `ind` | Indicator computed | `{ indicatorName, measure, values }` |
| `ind.mut` | Indicator mutated | `{ indicatorName, measure, values }` |

- `measure`: `{ kind, label, decimals? }`
- `values`: dictionary of named outputs

---

## 4. SQLite Schemas

### index.sqlite (per-run, co-located with JSONL)

```sql
CREATE TABLE events (
    sq  INTEGER NOT NULL,    -- sequence number
    ts  TEXT    NOT NULL,    -- ISO 8601 timestamp
    _t  TEXT    NOT NULL,    -- event type
    src TEXT    NOT NULL,    -- source component
    raw TEXT    NOT NULL     -- full JSON line
);
-- Indexes: ix_events_sq, ix_events_ts, ix_events_t, ix_events_src
```

### trades.sqlite (shared across runs)

```sql
CREATE TABLE runs (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    run_folder    TEXT UNIQUE NOT NULL,
    strategy      TEXT NOT NULL,
    version       TEXT NOT NULL,
    asset         TEXT NOT NULL,
    start_time    TEXT NOT NULL,
    end_time      TEXT NOT NULL,
    initial_cash  INTEGER NOT NULL,
    mode          TEXT NOT NULL,
    params_json   TEXT,
    total_bars    INTEGER NOT NULL,
    final_equity  INTEGER NOT NULL,
    total_fills   INTEGER NOT NULL,
    duration_ms   INTEGER NOT NULL,
    run_timestamp TEXT NOT NULL
);

CREATE TABLE orders (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id       INTEGER NOT NULL REFERENCES runs(id),
    order_id     INTEGER NOT NULL,
    asset        TEXT NOT NULL,
    side         TEXT NOT NULL,
    type         TEXT NOT NULL,
    quantity     TEXT NOT NULL,
    limit_price  INTEGER,
    stop_price   INTEGER,
    status       TEXT NOT NULL,    -- placed | filled | cancelled | rejected
    submitted_at TEXT NOT NULL
);

CREATE TABLE trades (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id      INTEGER NOT NULL REFERENCES runs(id),
    order_id    INTEGER NOT NULL,
    asset       TEXT NOT NULL,
    side        TEXT NOT NULL,
    price       INTEGER NOT NULL,
    quantity    TEXT NOT NULL,
    commission  INTEGER NOT NULL,
    timestamp   TEXT NOT NULL
);
```

---

## 5. Recommended Query Patterns

### jq (JSONL)

```bash
# Count events by type
jq -s 'group_by(._t) | map({type: .[0]._t, count: length})' events.jsonl

# Get all order fills
jq 'select(._t == "ord.fill")' events.jsonl

# Get events in sequence range
jq 'select(.sq >= 10 and .sq <= 20)' events.jsonl

# Extract fill prices
jq 'select(._t == "ord.fill") | {sq, price: .d.price, qty: .d.quantity}' events.jsonl

# Find errors and warnings
jq 'select(._t == "err" or ._t == "warn")' events.jsonl

# Trace a specific order lifecycle
jq 'select(.d.orderId == 1)' events.jsonl

# Get bar OHLCV series
jq 'select(._t == "bar") | [.d.open, .d.high, .d.low, .d.close, .d.volume]' events.jsonl
```

### sqlite3 (index.sqlite)

```sql
-- Count events by type
SELECT _t, COUNT(*) FROM events GROUP BY _t ORDER BY COUNT(*) DESC;

-- Get all order fills
SELECT raw FROM events WHERE _t = 'ord.fill';

-- Events in sequence range
SELECT * FROM events WHERE sq BETWEEN 100 AND 200 ORDER BY sq;

-- Events around a specific time
SELECT * FROM events WHERE ts >= '2024-01-15T10:00:00' AND ts < '2024-01-15T11:00:00';

-- Search by source
SELECT * FROM events WHERE src LIKE 'indicator.%';

-- Last N events
SELECT * FROM events ORDER BY sq DESC LIMIT 20;
```

### sqlite3 (trades.sqlite)

```sql
-- List all runs
SELECT id, strategy, asset, total_fills, final_equity FROM runs ORDER BY run_timestamp DESC;

-- Compare strategies
SELECT strategy, AVG(final_equity - initial_cash) AS avg_pnl, COUNT(*) AS runs
FROM runs GROUP BY strategy;

-- Get all trades for a run
SELECT t.* FROM trades t JOIN runs r ON t.run_id = r.id
WHERE r.run_folder = 'MyStrat_v1_AAPL_2024-2024_abc123_20240101T120000';

-- Win rate: count profitable vs losing fills
SELECT r.strategy,
  SUM(CASE WHEN t.side = 'sell' AND t.price > o.limit_price THEN 1 ELSE 0 END) AS wins,
  COUNT(*) AS total
FROM trades t
JOIN orders o ON t.run_id = o.run_id AND t.order_id = o.order_id
JOIN runs r ON t.run_id = r.id
GROUP BY r.strategy;

-- Order status breakdown
SELECT status, COUNT(*) FROM orders GROUP BY status;
```

---

## 6. Debugging Workflow Decision Tree

```
START: What's the problem?
  |
  +-- "Wrong P&L / equity"
  |     1. Check run.end event: jq 'select(._t == "run.end")' events.jsonl
  |     2. Verify fills match expectations:
  |        SELECT raw FROM events WHERE _t = 'ord.fill';
  |     3. Check position events for realizedPnl:
  |        jq 'select(._t == "pos") | .d' events.jsonl
  |     4. Compare fill prices to bar OHLCV at that timestamp
  |
  +-- "Order not filled"
  |     1. Find the order: jq 'select(.d.orderId == <ID>)' events.jsonl
  |     2. Check for ord.reject or ord.cancel events
  |     3. Check risk event: jq 'select(._t == "risk")' events.jsonl
  |     4. Verify limit price vs bar prices at next bar
  |
  +-- "Strategy not trading"
  |     1. Check if any ord.place events exist:
  |        SELECT COUNT(*) FROM events WHERE _t = 'ord.place';
  |     2. Check for signal events:
  |        jq 'select(._t == "sig")' events.jsonl
  |     3. Verify bar data is being received:
  |        SELECT COUNT(*) FROM events WHERE _t = 'bar';
  |     4. Check for errors/warnings:
  |        SELECT raw FROM events WHERE _t IN ('err', 'warn');
  |
  +-- "Indicator values wrong"
  |     1. Get indicator events:
  |        jq 'select(._t == "ind" and .d.indicatorName == "<NAME>")' events.jsonl
  |     2. Compare values at specific bars:
  |        SELECT e1.raw, e2.raw FROM events e1
  |        JOIN events e2 ON e2.sq = e1.sq + 1
  |        WHERE e1._t = 'bar' LIMIT 5;
  |     3. Check for ind.mut events (intra-bar updates)
  |
  +-- "Compare two runs"
  |     1. Query trades.sqlite for both runs:
  |        SELECT * FROM runs WHERE run_folder IN ('run1', 'run2');
  |     2. Compare fill counts and equity:
  |        SELECT strategy, total_fills, final_equity FROM runs;
  |     3. Diff individual trades if needed
  |
  +-- "Crash / incomplete run"
        1. Check if run.end exists:
           jq 'select(._t == "run.end")' events.jsonl
        2. Find last event: tail -1 events.jsonl
        3. Check for error events:
           jq 'select(._t == "err")' events.jsonl
        4. Rebuild index if missing:
           Use IEventIndexBuilder.Rebuild(runFolderPath)
        5. Re-extract trades:
           Use ITradeDbWriter.RebuildFromJsonl(runFolderPath, identity, summary)
```

---

## 7. Recovery Commands

If SQLite artifacts are missing or corrupt, they can be rebuilt from the JSONL source of truth:

- **Rebuild index**: `IEventIndexBuilder.Rebuild(runFolderPath)` — deletes and recreates `index.sqlite`
- **Rebuild trades**: `ITradeDbWriter.RebuildFromJsonl(runFolderPath, identity, summary)` — deletes run data and re-inserts

Both operations are idempotent and transactional.
