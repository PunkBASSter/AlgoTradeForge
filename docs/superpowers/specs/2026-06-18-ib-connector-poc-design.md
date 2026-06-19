# Interactive Brokers Paper-Trading Connector POC (Linux Container) — Design

**Date:** 2026-06-18
**Status:** Approved (brainstorming) — ready for implementation plan
**Scope:** Throwaway exploration spike. Disposable code; findings feed the future `LiveHost@ib`.

## Context

**Why:** The service-decomposition vision (`docs/service-decomposition-vision.md`, Q7) reserves a future
`LiveHost@ib` instance "with its IB Gateway sidecar" for the single-session venue class. Before committing
to that work, we need to **de-risk the entire IB path end-to-end**: can we run IB Gateway headless in a
Linux container, log into a paper account unattended, stream market data, aggregate it, and drive a full
order lifecycle to fills — all from C#? Interactive Brokers is architecturally unlike the existing Binance
connector (no REST/WebSocket; a single persistent binary socket to a running Gateway, with an `EWrapper`
callback model), so this is a genuine unknown worth a spike.

**Goal:** A throwaway C# console POC that proves the E2E path: **connect → resolve contract → stream
ticks + real-time bars → aggregate ticks into candles → place a market order to a fill → place + cancel a
resting limit order → place a bracket (entry + TP + SL) → read back positions/account → clean disconnect.**
Findings (working Dockerfiles, connection sequence, callback wiring, pitfalls) feed the real `LiveHost@ib`
later; the code itself is disposable.

**Decisions locked:**
- Throwaway exploration spike, C#/.NET, **outside** `AlgoTradeForge.slnx`, in `poc/ib-connector/`.
- Official **TWS API (IBApi C#)** — socket + `EWrapper`, not Client Portal Web API.
- **Real IB paper account**, live connection; real-time market data (subscriptions available).
- Instrument: **AAPL** (`STK`/`SMART`/`USD`). Real-time ticks only flow during US market hours.
- Order scope: **market round-trip + limit-then-cancel + bracket (entry/TP/SL)** — exercise as much as is safe on paper.
- Topology: **IB Gateway + IBC sidecar container** + **C# client container**, wired with docker-compose.

## Out of Scope (explicitly)

- No wiring into `ILiveConnector` / `IExchangeOrderClient` / `IInt64BarStrategy` (that's the real
  `LiveHost@ib` work, M3-era). May *demonstrate* mapping a tick into `Int64Bar`/`ScaleContext` as a
  forward-reference, but no Domain project changes.
- No `live-md/{venue}/` JSONL relay (Q7 relay contract) — noted as a follow-up, not built here.
- No live (real-money) trading; no 2FA/IBKey automation (paper only).
- No changes to `AlgoTradeForge.slnx`, CI, or any existing project.

## Architecture

```
┌──────────────────────────┐        socket          ┌──────────────────────────┐
│  ib-gateway (container)  │  4002 (paper) via socat │   ib-poc (container)     │
│  IB Gateway (headless)   │ <─────────────────────> │   .NET 10 console app    │
│  + IBC (auto-login,      │   EClientSocket /        │   EWrapper callbacks +   │
│    dialog dismissal)     │   EReader binary proto   │   EReader signal thread  │
│  + Xvfb + socat shim     │                          │                          │
└──────────────────────────┘                          └──────────────────────────┘
        ▲  creds via .env (TWS_USERID / TWS_PASSWORD, TRADING_MODE=paper)
        └─ docker-compose network; client depends_on gateway (port-ready gate)
```

- **Gateway container:** use the maintained `ghcr.io/gnzsnz/ib-gateway` image (bundles IB Gateway, IBC,
  Xvfb, and a `socat` API-port shim — the de-facto standard for headless IB on Linux). Config via env:
  `TWS_USERID`, `TWS_PASSWORD`, `TRADING_MODE=paper`, `READ_ONLY_API=no` (must be off to place orders),
  optional `VNC_SERVER_PASSWORD` for debugging the GUI. Exposes the paper API port to the compose network.
- **Client container:** `mcr.microsoft.com/dotnet/sdk:10.0` (or runtime + multistage). Runs the console app,
  connects to `ib-gateway:<port>` clientId `N`.

## Components (`poc/ib-connector/`)

```
poc/ib-connector/
  README.md                     # run instructions, prerequisites, market-hours caveat
  docker-compose.yml            # ib-gateway + ib-poc, .env, network, depends_on gate
  .env.example                  # TWS_USERID=, TWS_PASSWORD=, TRADING_MODE=paper  (real .env gitignored)
  .gitignore                    # .env, bin/, obj/
  Dockerfile                    # multistage build of the C# client
  src/
    IbPoc.csproj                # net10.0 console; references IBApi
    ibapi/                      # vendored official IBApi C# source (compiled as part of csproj)
    Program.cs                  # orchestration: staged demo with CLI flags / phases
    IbConnection.cs             # eConnect + EReader signal-thread boilerplate, lifecycle
    DemoWrapper.cs              # EWrapper impl — only the callbacks we use, rest no-op
    Contracts.cs                # AAPL STK contract + reqContractDetails conId resolution
    MarketData.cs               # reqMktData / reqTickByTickData / reqRealTimeBars wiring
    CandleAggregator.cs         # tick → N-second candle aggregation (the "processing" example)
    Orders.cs                   # market, limit+cancel, and bracket (parent/TP/SL via parentId + OCA)
    Logging.cs                  # timestamped console logging used as E2E evidence
```

**IBApi library:** vendor the official TWS API C# client **source** under `src/ibapi/` and compile it into
the project (the source is plain C#, no Windows dependencies — builds and runs on Linux/.NET 10). This avoids
depending on an unofficial NuGet mirror and is exactly the code `LiveHost@ib` would later reference. README
documents the source version/origin (interactivebrokers.github.io stable).

**Connection sequence (must work first, before any trading):**
1. `EClientSocket.eConnect(host, port, clientId)`
2. start `EReader`, pump messages on a dedicated thread via the `EReaderSignal`
3. await `connectAck` → `nextValidId(orderId)` (the starting order id; increment locally per order)
4. handle informational "errors" 2104/2106/2158 (data-farm connected) as non-fatal

**Market data + aggregation:**
- `reqMarketDataType(1)` real-time primary; fall back to `(4)` delayed-frozen off-hours.
- `reqTickByTickData(..., "AllLast")` and/or `reqMktData` → `tickPrice`/`tickSize` callbacks.
- `reqRealTimeBars(.., 5, "TRADES", ..)` (IB only supports 5-second RTBs) for native candles.
- `reqHistoricalData(.., keepUpToDate:true)` as the off-hours / arbitrary-bar-size candle path.
- `CandleAggregator` folds `AllLast` ticks into N-second OHLCV candles in-process — the explicit
  "tick/candle data processing" deliverable; logs each completed candle.

**Order lifecycle (paper):**
- **Market round-trip:** `placeOrder` BUY 1 AAPL `MKT` → observe `openOrder` → `orderStatus`
  (PreSubmitted→Submitted→Filled) → `execDetails` → `commissionReport`.
- **Limit + cancel:** `LMT` far from market (won't fill) → confirm resting `Submitted` → `cancelOrder` →
  confirm `Cancelled`.
- **Bracket:** parent entry + child take-profit `LMT` (opposite side) + child stop `STP`, linked by
  `parentId`, `Transmit=false` until the final child, TP/SL grouped via an OCA group. Mirrors the repo's
  `TakeProfitLevels` / `StopLossPrice` shape as a forward-reference. Use tiny size; optionally cancel the
  bracket at the end to leave the paper account flat.
- **Read-back:** `reqPositions` + `reqAccountSummary` to show resulting state; clean `eDisconnect`.

## Key Pitfalls (designed-around, documented in README)

- **Market hours:** real-time AAPL ticks only during RTH/extended hours; delayed-frozen + historical
  `keepUpToDate` keeps the data path exercisable off-hours. README states this loudly.
- **`READ_ONLY_API=no`** required or `placeOrder` is silently refused.
- **Paper port 4002** (live 4001); the gnzsnz image remaps via socat — pin the exposed port explicitly.
- **clientId uniqueness;** stale Gateway sessions; `nextValidId` must arrive before ordering.
- **Pacing:** avoid burst requests; resolve the contract once via `reqContractDetails`.
- **Startup race:** client must wait until Gateway's API port is accepting connections (compose
  `depends_on` + a small connect-retry loop in `IbConnection`).
- **EReader/signal boilerplate** is mandatory — without the signal pump, no callbacks arrive.

## Verification (E2E — the definition of done)

From `poc/ib-connector/`:
1. Copy `.env.example` → `.env`, fill paper `TWS_USERID`/`TWS_PASSWORD`.
2. `docker compose up --build`.
3. Confirm in logs, in order (this sequence IS the verification):
   - Gateway/IBC reports successful **paper login** (optionally verify via VNC).
   - Client logs **`nextValidId`** received → handshake proven.
   - **Contract resolved** (conId for AAPL).
   - **Ticks + 5s real-time bars** logged (real-time if market open, else delayed/historical fallback).
   - **Aggregated candle** lines printed by `CandleAggregator`.
   - **Market order** → `orderStatus Filled` + `commissionReport`.
   - **Limit order** → `Submitted` → `Cancelled`.
   - **Bracket** → parent + TP + SL appear in `openOrder`/`orderStatus`.
   - **Positions / account summary** printed; clean disconnect.
4. Capture the console log as the artifact proving the path.

Phased bring-up to avoid debugging everything at once: **(A)** Gateway container logs in → **(B)** client gets
`nextValidId` → **(C)** market data + aggregation → **(D)** market order to fill → **(E)** limit+cancel →
**(F)** bracket → **(G)** read-back/disconnect. Each phase is independently runnable via a `Program.cs` flag.

## Follow-ups (noted, not in this POC)

- Map the proven connection/callback wiring onto `ILiveConnector` for real `LiveHost@ib`.
- Implement the Q7 `live-md/ib/` append-only JSONL relay (LiveHost publishes raw, HistoryLoader tails).
- 2FA/IBKey handling for live (non-paper) sessions.
