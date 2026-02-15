# AlgoTradeForge
Fast track from ideas to live trading

# TODO:
- Domain model and app - design history data feed for the strategy

BacktestEngine needs
- IBarSource
- IBarStrategy
- BacktestOptions

TODO:
- Migrate indicator abstractions from S#
- Migrate DeltaZigZag, DzzPeak, DzzTrough
- Migrate DzzBreakoutStrategy, DzzPeakTroughStrategy
- Backtest running with Start/Stop/Pause/Resume/Next Bar/Next Trade API
- Strategy must track entry orders and exit orders (multi SL/TP) on its own (migrate from SS.AB)
- Add termination conditions to BacktestOptions
- History loading job
- Migrate ZZ strategies with indicator basics
- Add event streaming from backtest to DB in debug mode
- Connect web UI to backtest jobs and history loading jobs
