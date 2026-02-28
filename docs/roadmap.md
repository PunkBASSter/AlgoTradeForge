# Main focus:

## Launch optimized but not overtrained Delta ZigZag Breakout to live on multiple (30+) assets: crypto, stocks, maybe FX, maybe FUT.

Challenges:
1. Not that much candle history (I want 30+ of different assets) - INGESTOR -DEEP_RESEARCH IN PROGRESS
2. Candle Partition files don't have TF info in the name - INGESTOR
3. Lack of (free) data (Alpha vantage, Yahoo finance) - INGESTOR
  - Rate limiting by provider/token info
  - Scaling for splits with stock event data
  - 
4. Lack of optimizeable modules - DEV
  - ATR-based volatility filters
  - Timing-based exit modules (close at eod, eow)

5. Lack of testing (manual) - TEST
  - TEST DEBUG
  - TEST OPTIMIZATION

## Candle Ingestor
Upd CandleIngestor - prepare for using on server: use DI, host as WebAPI for control with scheduling config; update partitioning - add TF in file names; add ingestors for stock data (alpha vantage, yahoo finance); ingest events metadata and add a scaling factor for splits;

## Split domain / API
Split strategies assembly and backtesting; maybe extract a shared contracts assembly
Split strategy API (local, actively developed) from platform API (remote - stable, multi-user). FE calls 2 APIs. Don't forget CORS.

## Unify serialization, validation and error handling
1. Ensure there are camelCase serializers for API
2. Add validators layer to check params input
3. Add global exception handler
4. Introduce Result<> pattern?