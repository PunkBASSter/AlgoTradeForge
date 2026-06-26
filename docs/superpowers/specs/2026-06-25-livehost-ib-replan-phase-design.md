# LiveHost — IB Re-plan Phase — Design

**Date:** 2026-06-25
**Status:** Approved (brainstorming) — decomposes into per-plan specs/plans
**Scope:** Re-frame the remaining LiveHost decomposition work around **Interactive Brokers as the first real single-session venue**, driving to a **working `LiveHost@ib` (paper)** that both collects IB ticks and executes paper orders end-to-end. Specifies the cross-cutting abstraction updates the IB venue forces, and decomposes the work into dependency-ordered plans (each gets its own brainstorm → writing-plans → SDD; this doc is their shared spine).

## Context

**Why now.** Plans 1–4 + 6a built the entire single-session four-planes architecture (`2026-06-20-livehost-collection-execution-design.md`) — `IVenueConnector`, `ITickRouter`/`IStrategyDispatch`, the bounded-channel data plane, `collection.json` config — but every one of them runs against **Binance**, which is a *concurrent* venue: the **degenerate case** of the model the seams were built for. The motivating venue (IB) has never touched the production code. The remaining two deferred items (Plan 5 multi-account order routing; Plan 6b M6 partial-bar seeding) are exactly the pieces that only *matter* for, or are validated by, a single-session venue.

**The decision (this brainstorming).** Rather than build Plan 5's `IOrderRouter`/multi-account abstraction blind to its real consumer, we re-plan around IB now: the order-routing seam, the `Asset` contract-identity seam, and the data-plane push seam are all **designed against IB's actual requirements** (surfaced by the connector POC) and **validated by a real `LiveHost@ib`** — Binance single-account/single-session becomes the degenerate case it always was.

**De-risking already done.** The IB connector POC (`poc/ib-connector/`, `[[project_ib_connector_poc]]`) proves the full paper-trading E2E path from a Linux container: `connectAck → nextValidId → contractDetails(conId) → tick-by-tick + 5s realtime bars → market/limit/bracket orders → Filled/Cancelled lifecycle → position flatten`. Every IB boundary below is taken from working POC code, not research.

**Owner directive (unchanged from the housekeeping/Plan-5/6 phase):** NOT in production. Break anything for the cleanest end-state — no back-compat shims, no dead bridges. "Clean" still means correct + tested: the backtest golden suites and the live data-plane tests are the behavior guard.

## What IB forces — POC findings mapped to production seams

Every row is grounded in `poc/ib-connector/src/`:

| IB reality (POC source) | Production seam to touch | Plan |
|---|---|---|
| `EWrapper` callbacks (push) on the `EReader` pump thread — `tickByTickAllLast → OnTrade`, `realtimeBar → OnRealtimeBar` | `IVenueConnector` data-entry — resolved as **push→channel→pull bridge** (below) | 3 |
| One `EClientSocket` carries data **and** orders (`reqTickByTickData` + `placeOrder` same connection) | New **single-session** model: per-venue session owns the transport; data + order planes share it; `MarketDataSessionPolicy` capability gates wiring | 3, 4 |
| `Contract { Symbol, SecType, Exchange, PrimaryExch, Currency, ConId, LocalSymbol }`; `ConId` resolved at runtime via `reqContractDetails` | **Venue contract model** (`IbContract`) in the IB slice, bound to a reused Domain `Asset` by a boundary mapper | 1 |
| `placeOrder`/`cancelOrder(int, OrderCancel)`/bracket; client-assigned monotonic order ids from `nextValidId`; `Tif` mandatory (10052 if empty) | `IOrderRouter` + account-scoped `LiveOrderContext` | 2, 4 |
| On a fresh connection IB **pushes back all open orders** server-side before any request — broker is source of truth | Existing 3-phase `OrderGroupReconciler`, run **per order session** | 2, 4 |
| `reqAccountSummary(reqId, "All", …)`, `position(account, contract, pos, avgCost)` — account is per-position | **Multi-account routing key**; account-scoped portfolio init | 2 |
| `reqRealTimeBars(…, 5, "TRADES", …)` — venue-published 5s bars | `IBarSourceResolver` venue-published-bar case (§A½), alongside Binance kline | 3 |
| gnzsnz/ib-gateway sidecar + IBC headless auto-login; paper API on socat-exposed `:4004`; `clientId` unique per connection | Deployment: `ATF_PROFILE=ib`, compose, livehost manual-approval gate | 5 |
| Live login needs IB Key / IBKR Mobile 2FA; **paper needs none** | Separate **LIVE-ONLY** spike — non-blocking for the paper endpoint | S |

## Cross-cutting abstraction decisions (locked this brainstorming)

### 1. Data seam — push→channel→pull bridge
IB is callback-driven; our `IVenueConnector` is a pull seam (`IAsyncEnumerable<IMarketEvent>`, used by `RelayIngest.Pump`). The IB connector **bridges**: `EWrapper` callbacks write canonical events into a **bounded channel**; `IbVenueConnector` drains it as the existing `IAsyncEnumerable<IMarketEvent>`. The seam, `BinanceVenueConnector`, and `RelayIngest.Pump` are **untouched**.

*Why not flip the seam to push/visitor:* §G of the four-planes doc flagged that upgrade "at IB onboarding **or** if a firehose shows sustained seam alloc >~50 MB/s." IB is **low-rate** — market-data lines throttle to ~250 ms snapshots, ~4 updates/s/instrument — so the firehose/GC argument does not bite, and the §G analytic worst case (~128 KB/s Gen0) already cleared the pull seam. The channel hop is negligible at IB rates; flipping the seam would reshape Binance + `RelayIngest` for no IB benefit. (Revisit only when a genuine high-rate venue — not IB — arrives.)

### 2. Contract identity — reuse Domain `Asset`, venue slice owns the contract model
The Domain `Asset` hierarchy (`Assets/`) is the **polymorphic strategy/economics model**: strategies, `Portfolio`, settlement (`CashAndCarrySettlement`/`MarginSettlement`), and `ComputeAutoApplyDelta` dispatch on asset *kind*. It is **reused wherever it serves** — an IB AAPL position *is* an `EquityAsset` → cash-and-carry, zero new domain logic.

Where Domain genuinely cannot express something — IB's `ConId`, `SMART` routing destination, `SecType` string, `Currency`, `LocalSymbol`, listing `PrimaryExch` vs routing `Exchange` — the **IB slice owns its own contract model** (`IbContract`, in IB Infrastructure), bound to the reused Domain `Asset` by a **boundary mapper**. Domain gains **no** IB vocabulary and keeps ZERO ProjectReferences.

This mirrors the codebase's existing three-layer pattern (`BinanceAggTrade` → `TradeTick` → `FeedSeries` sidecar), now applied to instrument identity: `IbContract` (venue model) ↔ `EquityAsset` (Domain) via a mapper at the slice boundary. The door is open for future single-session slices (e.g. a `FixContract`) to define their own contract model **only when Domain can't be reused**.

**Two-tier identity:** a *configured* tier (`{Symbol, SecType, Exchange, PrimaryExch, Currency}` — what the user types, round-trips through `collection.json`/session config) and a *resolved* tier (`ConId`, handed back by `reqContractDetails`, resolved+cached at runtime by an Infrastructure `IbContractResolver`). One configured identity resolves twice — to a Domain `Asset` for the strategy and to an `IbContract`+`conId` for the connector — neither resolution leaking into the other.

**Deferred (flagged, not built):** multi-currency. `IbContract` carries `Currency`; paper AAPL/USD is single-currency and trivial. A multi-currency IB account eventually needs currency in PnL/portfolio math (a Domain-adjacent concern). Out of scope for the paper endpoint; revisit when a non-USD instrument goes live.

### 3. Single-session — per-venue session owns the transport
A single-session venue is modeled by a per-venue **`IbSession` that owns the one `EClientSocket` + `EReader` pump thread**. The `IbVenueConnector` (data plane) and the `IbOrderSession` (execution plane) both hold a handle to it. A **`MarketDataSessionPolicy` capability** (`Concurrent` | `SingleSession`, already named in the service-decomposition vision) selects the host wiring:
- **Binance (`Concurrent`):** two independent connections — `BinanceVenueConnector` (data) and `BinanceLiveConnector` (orders) — unchanged.
- **IB (`SingleSession`):** one `IbSession`; data and order planes share it; collection and execution finally cohabit one process (the entire reason the four-planes design exists).

The internal ownership detail (who reads the pump, how writes are serialized onto the socket) is a Plan 3/4 decision, not a roadmap one — but the bounded-channel and exec-priority invariants from Plan 4 (`MarketDataQueue` `DropNewest`, `EventQueue` `FullMode.Wait`, single processing task with exec priority) carry over.

## Decomposition (dependency-ordered; each = own brainstorm → writing-plans → SDD)

| # | Plan | Owns | Depends on | Review tier |
|---|------|------|-----------|-------------|
| **0** | ~~**M6 partial-bar seeding** (was "6b")~~ — **SUPERSEDED / DROPPED** | Persisting mid-bar accumulator state as CAS JSON was built (10 commits) then **reverted** as over-engineered: the partial bar is redundant with the already-lossless tick archive. M6 mid-bar continuity was reframed as the **live reconnect/catch-up replay** mechanism — now DESIGNED + IMPLEMENTED (11-task SDD) + **MERGED** (core + Binance; `2026-06-25-livehost-reconnect-catchup-replay-design.md` / `-replay.md`). The partial bar is rebuilt by replaying archived source records from the last completed bar — a free by-product of catch-up, bounded by one partial bar. **7 alt-bar families pass the M6 golden; Renko deferred + fenced** (cross-bar `_pendingVolume` needs the path-dependent ReplayBoundary — replay open point #7). The old Renko `long` seam is gone. | — | **DONE (merged as catch-up replay)** |
| **1** | **Venue-neutral contract identity** | `IbContract` venue model in the IB slice; boundary mapper Domain `Asset` ↔ `IbContract`; `IbContractResolver` (conId resolution + cache); any genuinely-neutral Domain addition (e.g. `Currency`) only if forced. Configured-tier identity round-trips through config. | — | sonnet |
| **2** | **Plan 5: `IOrderRouter` + multi-account** | Account-keyed `IOrderRouter` (processing task → router → per-account execution); account-scoped `LiveOrderContext`; **per-target `ScaleContext`** (each target scales off ITS OWN asset — removes the Plan-4 single-`ScaleContext`-for-all assumption); per-session 3-phase reconciliation; strategy binding `{ dataSubscriptions[], executionAccount }`. Binance single-account = degenerate case (the general model with one account). | 1 | **opus** (order-path) |
| **3** | **`IbVenueConnector` (data) + single-session** | `IbSession` owns `EClientSocket`+`EReader`; `IbVenueConnector` bridges `EWrapper` callbacks → bounded channel → `IAsyncEnumerable<IMarketEvent>` (tick-by-tick `TradeTick` + `reqRealTimeBars` venue-published bars + contract resolution); relay archival of IB ticks; `IBarSourceResolver` venue-bar case; `MarketDataSessionPolicy` host wiring. | 1 | sonnet/opus |
| **4** | **IB order session (execution) + cohabit** | `IbOrderSession` plugged into the `IOrderRouter` (2), **sharing the `IbSession` socket** from (3): `placeOrder`/`cancelOrder`/bracket via `EWrapper`, `Tif=DAY`, client-assigned order ids, fills via `orderStatus`/`execDetails`/`commissionAndFeesReport`, per-session reconciliation against IB's server-side open-order pushback. Collect + execute cohabit one process. | 2, 3 | **opus** (concurrency/order) |
| **5** | **Deployment: `LiveHost@ib` runtime** | gnzsnz/ib-gateway sidecar in compose; `ATF_PROFILE=ib`; livehost manual-approval gate (never auto-pull the money host); real paper E2E run (reuses the POC's verified credential/port/clientId facts). | 4 | controller + owner |
| **S** | **Live-2FA spike** (LIVE-ONLY) | IBKR Mobile push approval at session start / kept-alive session. Paper needs none — does **not** block the paper endpoint. | — | spike |

**Dependency spine:** `1 → {2, 3} → 4 → 5`. Spike S floats free. The **working paper `LiveHost@ib`** endpoint is reached at **Plan 5**.

**Sequencing notes:**
- **Plan 0 dropped → replaced by catch-up replay (MERGED).** The persisted-state seeding approach was reverted (redundant with the tick archive); the **live reconnect/catch-up replay** mechanism (core + Binance) was designed, implemented via 11-task SDD, and **merged** (~2026-06-26). It rebuilds the partial by replaying archived records, with aggId-watermark gap detection + policy-B backfill seam for true disconnects; Renko catch-up is deferred + fenced (cold path) pending its path-dependent ReplayBoundary. IB-specific data-plane replay + IB reconnect trigger remain in Plan 3/4. **NEXT is Plan 1 (venue-neutral contract identity) — the spine root that unblocks Plans 2 & 3.**
- **Plans 3 and 4 are split** (data plane vs order session) though they share the `IbSession` socket, because the two planes have very different review profiles — Plan 4 is order-integrity/concurrency-critical (opus), Plan 3 is mostly ingest plumbing.
- **§E cosmetic cleanups** (unused `_logger` in `TickRouter`/`StrategyDispatch`; `Program.cs` FQ DI names; UTF-8 BOM on relocated engine test files; double-`ToList`) fold in opportunistically where a file is already open.

## Conventions / gotchas (carry from the prior phase)

- ONE `dotnet` process at a time (build/test strictly sequential). `powershell.exe`, not `pwsh`.
- Domain stays ZERO ProjectReferences and venue-neutral. LiveHost must not depend on HistoryLoader; `Live.Relay` must not depend on LiveHost. Every channel bounded; order/execution path independent of market data.
- No `catch when (ex is not OperationCanceledException)` in long-running loops — use `IsTrueShutdown(ex, ct)` (`[[feedback_oce_filter_pattern]]`). No sync-over-async at prod call sites. **No `Async` suffix** on new async methods (`[[feedback_no_async_suffix]]`). using-over-try/finally. One type per file.
- Int64 money: `MoneyConvert.ToLong` in Domain, `ScaleContext` at boundaries; per-target routing scales each target off ITS OWN asset (the Plan-4 single-`ScaleContext` assumption is exactly what Plan 2 removes); the IB connector does INDEPENDENT price/qty scaling (mirror `BinanceVenueConnector.TickScale`).
- Perf/alloc regressions go through the BenchmarkDotNet harness, never ad-hoc asserts. Never benchmark while another `dotnet` runs.
- Commits: standing no-auto-staging (`[[feedback_no_auto_staging]]`); SDD implementer `git add` is hook-DENIED — the CONTROLLER stages + commits after verifying the diff, with explicit per-branch owner authorization. Commit messages via bash heredoc + `git commit -F` (never PowerShell Out-File — UTF-8 BOM); end with the Co-Authored-By + Claude-Session trailers.
- IB vendored API facts (from POC, feed Plan 3/4): TWS API **10.45.01**, needs `Google.Protobuf 3.29.5` + the `protobuf/` source; vendored IBApi compiles as a **separate nullable-off library** (forcing NRT on IB's pre-NRT source yields 100+ false errors); version-sensitive callbacks (`commissionAndFeesReport`, `cancelOrder(int, OrderCancel)`, `decimal` sizes, `long permId`); paper API port socat-exposed at `:4004`; `clientId` unique per concurrent connection.

## Verification

This is a phase/roadmap design doc. It is "done" when the owner signs off on the endpoint (working paper `LiveHost@ib`), the three locked cross-cutting abstraction decisions (data-seam bridge, contract identity incl. two-tier resolution, single-session transport), and the 6-plan + 1-spike decomposition with its dependency spine. Per-plan implementation verification lands in each plan's own spec:

- ~~**Plan 0:** M6 golden across a mid-bar restart~~ — **superseded**; mid-bar continuity is verified by the merged catch-up replay mechanism's M6 golden (`BatchEqualsLiveGoldenTests` — 7 alt-bar families; Renko deferred), not via persisted state.
- **Plan 1:** round-trip `IbContract` ↔ Domain `Asset` mapper; `IbContractResolver` resolves `{AAPL,STK,SMART,USD}` → conId (against POC-verified behavior).
- **Plan 2:** two-account routing test (strategy set X → account A, set Y → account B, shared A data; orders isolated per account); per-target scaling correctness.
- **Plan 3:** IB ticks → relay `.atft` → real `StreamCanonicalizer<TradeTick>` → canonical CSV row-exact (mirrors `LiveRoundTripTests`); venue-published 5s bars resolve via `IBarSourceResolver`.
- **Plan 4:** order lifecycle (market Filled, limit Submitted→Cancelled, bracket) against IB paper; reconciliation against server-side open-order pushback; single-session socket shared without order-path starvation.
- **Plan 5:** live paper E2E — `LiveHost@ib` collects AAPL ticks and executes a paper order, reusing the POC's verified credential/port/clientId path.

## Open points (deferred, flagged)

1. **Multi-currency PnL/portfolio** — `IbContract.Currency` exists; multi-currency account math is out of scope for the USD paper endpoint.
2. **IB market-data line economics** (~1000 instruments, snapshot cadence, booster packs) — a sizing exercise that bounds what "lossless" means on IB; informs Plan 3 but not blocking for a small executed subset.
3. **Live-2FA** (Spike S) — LIVE-ONLY; the paper endpoint never exercises it.
4. **Bounded-channel drop policy under sustained archival backpressure** (spill-to-disk then block) — inherited from the four-planes doc §K; measured with a real IB feed in Plan 3.
