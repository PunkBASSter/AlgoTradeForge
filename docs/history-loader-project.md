# History Loader as Candle Ingestor replacement

## Requirements & Key points

* Web API hosted to run in the background and be able to execute actions on demand
* Stick to clean architecture - keep separation by layers (api -> (handlers(logic) -> domain(config models, history models, common services/rules, data converters/aggregators)) <- adapter/connector/file_system), then by vertical slices (binance/spot/futures, etc.)
* Separate the assemblies inside History Loader similarly to AlgoTradeForge
* Using DI is a MUST
* Single-machine Quartz.net (or more modern lightweight alternative) job cron-based trigger to launch periodic actions
* Share disk with data consumers, e.g. BacktestEngine
* Use Options pattern to immediately react on config changes
* Config defines for each exchange/asset_types/asset
    Start date - End date (optional), prioritized list of timeframes for backfill in binance format ([1d, 1h, 1m] means we load days from start to) 
* Data feed-specific status.json (e.g. for open-interest data inside binance futures) having last loaded timestamp and listing all "holes" or missing data points in history dependent on the market (holes detection is out of scope now)
* Common asset-specific summary.json to store each data feed history period available (from start to end)

### Performed activity types
* Initial data load (backfill) of ASSET/TF/(START-END|NOW dates), launched only at first start or on demand.
* Periodic (most likely daily) loading of the previous period data - for spot markets, essentially same as backfill but for delta from last loaded timestamp (with rewriting it with non-null and non-empty data) to END or NOW 
* Live loading - receiving data from the live connectors reusing (at least partly) existing Live Connectors from infrastructure (TODO: ??challenge for two separately hosted projects - how to share an exchange connector??)

### Optional:
* (if common types are needed in BacktestEngine and History loader) Shared library with history data models
