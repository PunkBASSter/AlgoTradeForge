# IB Connector POC

Throwaway spike: proves the Interactive Brokers paper-trading E2E path from a Linux container.
See `docs/superpowers/specs/2026-06-18-ib-connector-poc-design.md` for the design and
`docs/superpowers/plans/2026-06-18-ib-connector-poc.md` for the implementation plan.

## Prerequisites
- An IB account with **paper trading** enabled (paper username/password).
- Real-time market-data subscription for US equities if you want real-time AAPL ticks;
  otherwise the POC falls back to delayed-frozen + historical data.
- Docker + docker-compose.

## One-time: vendor the IBApi source (gitignored, not committed)
The vendored source compiles as a separate `ibapi/IBApi.csproj` library (nullable off, to match
IB's settings). Only the `.cs` source is gitignored; the project file is tracked.
1. Download the **Stable** TWS API from https://interactivebrokers.github.io/
2. Unpack and copy:
   - every `*.cs` under `IBJts/source/CSharpClient/client/` → `ibapi/`
   - every `*.cs` under `IBJts/source/CSharpClient/client/protobuf/` → `ibapi/protobuf/`
   Do **not** copy `Properties/AssemblyInfo.cs`, `bin/`, `obj/`, or the original `CSharpAPI.csproj`.
3. The pinned dependency is `Google.Protobuf 3.29.5` (already referenced in `ibapi/IBApi.csproj`).
4. Vendored version: **10.45.01**.

## Run
1. `cp .env.example .env` and fill in your **paper** credentials.
2. `docker compose up --build`
3. Watch the logs for the verification sequence (see "Verification" below).

## ⚠️ Market hours
Real-time AAPL ticks only stream during US market hours (and extended hours if enabled).
Off-hours the POC uses `reqMarketDataType(4)` (delayed-frozen) and historical
`keepUpToDate` so the data/aggregation path still runs. Set `IB_REALTIME=false` off-hours.

## Verification (E2E definition of done)
With market open and real-time data: `IB_PHASE=all docker compose up --build`. Confirm, in order:
1. gateway/IBC logs a successful **paper login** (VNC into 127.0.0.1:5900 to watch the GUI if needed)
2. client logs `connectAck` → `nextValidId = N`
3. `contractDetails ... conId=...` for AAPL
4. `RTB 5s ...` and/or `AGG candle ...` lines
5. market order: `placeOrder MKT` → `orderStatus ... status=Filled` → `commissionAndFeesReport`
6. limit: `orderStatus ... status=Submitted` → `status=Cancelled`
7. bracket: three `openOrder` lines (parent + TP + SL)
8. `accountSummary ...` + `eDisconnect` + `done`

Run a single phase, e.g.: `IB_PHASE=connect docker compose up --build`.
Off-hours, set `IB_REALTIME=false` to exercise the data/aggregation path via delayed + historical data.

Local (no Docker) build + unit tests:
```
dotnet build src/IbPoc.csproj
dotnet test  tests/IbPoc.Tests.csproj
```

## Pitfalls
- `READ_ONLY_API=no` is required for order placement.
- Paper API port is **4002** (live would be 4001). If your gateway image maps the socket to a
  different port, override `IB_PORT` in `docker-compose.yml`.
- `clientId` must be unique per concurrent connection; a stale gateway session can block reconnects.
- Real-time equity ticks need a market-data subscription; otherwise set `IB_REALTIME=false`.
- The IBApi callback signatures are version-specific — this POC targets **10.45.01**
  (`commissionAndFeesReport`, `cancelOrder(int, OrderCancel)`, `decimal` sizes).
