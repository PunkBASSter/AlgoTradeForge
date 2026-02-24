Main focus:
Launch optimized but not overtrained Delta ZigZag Breakout to live on multiple (30+) assets: crypto, stocks, maybe FX, maybe FUT.

Challenges:
1. Not that much candle history (I want 30+ of different assets) - INGESTOR -DEEP_RESEARCH IN PROGRESS
2. Candle Partition files don't have TF info in the name - INGESTOR
3. Lack of (free) data (Alpha vantage, Yahoo finance) - INGESTOR
  - Rate limiting by provider/token info
  - Scaling for splits with stock event data
  - 
4. Lack of optimizeable modules - DEV
  - No ATR-based volatility filters
  - No reliable indicator export?

5. Lack of testing (manual) - TEST !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!



Upd CandleIngestor - prepare for using on server: use DI, host as WebAPI for control with scheduling config; update partitioning - add TF in file names; add ingestors for stock data (alpha vantage, yahoo finance); ingest events metadata and add a scaling factor for splits;

Split strategies assembly and backtesting; maybe extract a shared contracts assembly

Split strategy API (local, actively developed) from platform API (remote - stable, multi-user). FE calls 2 APIs. Don't forget CORS.