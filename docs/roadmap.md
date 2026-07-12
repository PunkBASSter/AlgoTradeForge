# Main focus:

## TECH: Split domain / API
Confirm cloud/local concerns and responsible services separation. - Build components dependency graph.
Split strategy API (local, actively developed) from platform API (remote - stable, multi-user). FE calls 2 APIs. Don't forget CORS.
@docs\split-local-remote-features.md
1. Decouple from local file system: replace with abstraction (to add S3 compatibility)
2. Add validators layer to check params input
3. Add global exception handler
4. Introduce Result<> pattern?

## HistoryLoader & Live mode cloud-native redesign (why? collect ticks 24/7)
Confirm cloud/local concerns and responsible services separation.
- Multi-exchange adapters (Gate.io, KuCoin): extension-point findings + direction @docs\history-loader-multi-exchange-adapters.md
Cloud:
- Make history loader an incremental (delta) batch-loader backfill tool
  - Replace local file system calls with abstraction
    - Implement for local file system
    - Implement for S3
- Introduce live host - sync processing of strategy logic (CPU) + async incremental data updates (IO)
  - Keep existing Binance API support
  - Add IB data connector
  - Light short-term incremental storage for new live data -> long-term S3 compatible
  - Support live host with X reserve nodes (either redundant data saving with sync later or stand by or something else)
- Collect raw ticks
- Collect market depth snapshots
- Implement cloud to local sync
- Determine observability strategy: cloud logging, monitoring, telemetry

Data processing (local?):
- Backfill from https://data.binance.vision/ via https://github.com/binance/binance-public-data
- Consider extracting data processing layers (raw, aggregated/transformed, analytics)
- Consider using parquet/clickhouse for storing data feeds as columns

- Where to store metadata? Keep in FS or some DB?


## Launch optimized but not overtrained Delta ZigZag Breakout to live on multiple (30+) assets: crypto, stocks, maybe FX, maybe FUT.

## QA
- Refactor existing ZigZagTrendBreakout with using the modules - TODO - restore initial logic
- Debug Donchian Strategy
- Debug and fix trend zigzag

## Live
@docs/live-connector-binance.md
- display the same indicator data as in debug mode

## Strategy modules
- `Strategy-framework.md`
- Timing-based exit modules (close at eod, eow)
- Kelly-based risk module

## Backtest/opt
- Metrics enhancement: display number of opening trades, slippage loss, commissions loss, average profit, average loss, mean/dispersion, etc.
- Delete backtest results
- Display run start time on details, sort by descending timestamp

## Warning system
- instead of blocking errors/exceptions warn about corrupt/insufficient data
- add validators layer (where?) for input data and for runtime (if input data is impossible)

## TODO:
- Claude additions on review - verify OOP compliance (explain what's expected)
- Missing skills, like OOP-style class design (explain)
- Documentation hooks (continuous readme/constitution/claude.md updates)
