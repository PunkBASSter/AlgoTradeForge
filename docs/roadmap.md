# Main focus:

## Launch optimized but not overtrained Delta ZigZag Breakout to live on multiple (30+) assets: crypto, stocks, maybe FX, maybe FUT.

## QA
- Debug and fix trend zigzag
- Add optimization Re-run button to expose the all same settings
- Add preview/quick popup for optimization run
- Estimate the data volume on the current runs, evaluate DB performance and further performance capacity

## Overfitting control
- Walk Forward OPTI
- Permutations test
- Overfitting evaluation, effective param ranges
- Implement other optimizers (genetic, bayesian, random search)

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

## TECH: Serialization, validation, error handling
1. Ensure there are camelCase serializers for API
2. Add validators layer to check params input
3. Add global exception handler
4. Introduce Result<> pattern?

## QA
- Test optimization with pluggable modules
- Test trades isolation module with multiple simultaneous orders allowed?

## Strategy development
- Add another strategy to the framework, maybe a simple mean reversion for trading in ranges
- Add a strategy with a different logic, e.g. price patterns, volume patterns, market profile etc.

## Usability
- Endpoint to generate default params via reflection

## TECH: Split domain / API
Split strategies assembly and backtesting; maybe extract a shared contracts assembly
Split strategy API (local, actively developed) from platform API (remote - stable, multi-user). FE calls 2 APIs. Don't forget CORS.
@docs\split-local-remote-features.md

## Candle Ingestor / History Loader
history-loader-project.md - DONE
### Stocks+futures IB support for history loader and live connector

## Warning system
- instead of blocking errors/exceptions warn about corrupt/insufficient data
- add validators layer (where?) for input data and for runtime (if input data is impossible)

## TODO:
- Claude additions on review - verify OOP compliance (explain what's expected)
- Missing skills, like OOP-style class design (explain)
- Documentation hooks (continuous readme/constitution/claude.md updates)

### ~~Fix compiler warnings~~ — DONE

### IDistributedCache -> IHybridCache (?)
