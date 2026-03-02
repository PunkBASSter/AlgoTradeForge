# Main focus:

## Launch optimized but not overtrained Delta ZigZag Breakout to live on multiple (30+) assets: crypto, stocks, maybe FX, maybe FUT.

## Strategy modules
  - Timing-based exit modules (close at eod, eow)
  - Multi-order strategy module, **Order Groups**
  - `Strategy-framework.md`

## Optimization
- Ability to make a cascade delete of optimization result and related runs
- Display optimization trials statistics instead of raw json with params (reuse the same view as on backtest tab for listing trials of an optimization)
- Walk Forward OPTI
- Permutations test
- Overfitting evaluation, effective param ranges

## Candle Ingestor
Upd CandleIngestor - prepare for using on server: use DI, host as WebAPI for control with scheduling config; update partitioning - add TF in file names; add ingestors for stock data (alpha vantage, yahoo finance); ingest events metadata and add a scaling factor for splits;
1. Not that much candle history (I want 30+ of different assets) - INGESTOR -DEEP_RESEARCH IN PROGRESS
2. Candle Partition files don't have TF info in the name - INGESTOR
3. Lack of (free) data (Alpha vantage, Yahoo finance) - INGESTOR
  - Rate limiting by provider/token info
  - Scaling for splits with stock event data

## TECH

### Split domain / API
Split strategies assembly and backtesting; maybe extract a shared contracts assembly
Split strategy API (local, actively developed) from platform API (remote - stable, multi-user). FE calls 2 APIs. Don't forget CORS.

### Unify serialization, validation and error handling
1. Ensure there are camelCase serializers for API
2. Add validators layer to check params input
3. Add global exception handler
4. Introduce Result<> pattern?

### IDistributedCache -> IHybridCache (?)
...