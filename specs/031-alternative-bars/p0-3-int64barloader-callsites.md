# P0-3 — `IInt64BarLoader` external-consumer audit (Phase 0)

**Status:** Complete. **24 callsites** total: 2 production, 22 tests, 0 in private repo.

## What was audited

Per the TRD §11 Phase 0 mandate: enumerate every consumer of `IInt64BarLoader.Load(...)` across the public + private repos, so the Phase 1a signature change to `Load(DataFeedDescriptor, DateOnly, DateOnly)` (TRD §9.5) has a bounded blast radius.

The audit is exhaustive — these are the callsites Phase 1a must update.

## Current interface

`src/AlgoTradeForge.Application/CandleIngestion/IInt64BarLoader.cs:7-15`

```csharp
TimeSeries<Int64Bar> Load(
    string dataRoot,
    string exchange,
    string symbol,
    DateOnly from,
    DateOnly to,
    TimeSpan interval);

DateTimeOffset? GetLastTimestamp(string dataRoot, string exchange, string symbol);
```

## Production callsites (2)

| File:Line | Context |
|---|---|
| `src/AlgoTradeForge.Infrastructure/History/CsvDataSource.cs:27` | Backtest data fetch via `HistoryDataQuery`. Source interval from `CandleStorageOptions.SourceInterval`. |
| `src/AlgoTradeForge.Infrastructure/History/HistoryRepository.cs:23` | Subscription-driven load via `DataSubscription`. |

Both call `barLoader.Load(dataRoot, exchange, AssetDirectoryName.From(asset), from, to, sourceInterval)`. Both will switch to constructing a `DataFeedDescriptor(dataRoot, exchange, AssetDirectoryName.From(asset), TimeFrameFormatter.Format(sourceInterval), DataFeedKind.TimeBar)` and passing that.

## Test callsites (22)

### `tests/AlgoTradeForge.Infrastructure.Tests/CandleIngestion/CsvInt64BarLoaderTests.cs` (6 — file deleted by P1a-31)

- `:45-48`, `:70-73`, `:88-91`, `:105-108`, `:116-119`, `:132-135`

### `tests/AlgoTradeForge.Infrastructure.Tests/History/PartitionedCsvBarLoaderTests.cs` (10)

- `:46-49`, `:72-75`, `:94-97`, `:112-115`, `:129-132`, `:147-150`, `:158-161`, `:239-242`, `:257-260`, `:277-280`, `:297-300`

### `tests/AlgoTradeForge.Infrastructure.Tests/History/CsvDataSourceTests.cs` (1 mock setup)

- `:37-41` — `_loader.Load(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<TimeSpan>())`. Will become `Arg.Any<DataFeedDescriptor>()` (or `Arg.Is<DataFeedDescriptor>(...)` with field constraints).

### `tests/AlgoTradeForge.Infrastructure.Tests/History/HistoryRepositoryTests.cs` (4 mock setups)

- `:43-44`, `:56-57`, `:78-79`, `:98-99` — same `Arg.Any<TimeSpan>()` mock pattern; all switch to descriptor.

## Private repo callsites

`grep -r "IInt64BarLoader" ../AlgoTradeForge.Private/` — **zero hits**. The private plugin assembly does not depend on the loader interface, so the breaking change has no plugin impact.

## Migration mechanics for Phase 1a (P1a-27)

For each test callsite:

1. Hand-rolled call (`_loader.Load(_testDataRoot, "Binance", "BTCUSDT", from, to, TimeSpan.FromMinutes(1))`)  
   → `_loader.Load(new DataFeedDescriptor(_testDataRoot, "Binance", "BTCUSDT", "1m", DataFeedKind.TimeBar), from, to)`
2. NSubstitute mock setup (`_loader.Load(Arg.Any<string>(), …, Arg.Any<TimeSpan>())`)  
   → `_loader.Load(Arg.Any<DataFeedDescriptor>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())`

Production callsites (`CsvDataSource`, `HistoryRepository`) build the descriptor inline at the call site.

## Conclusion

The 24-callsite blast radius is bounded. `CsvInt64BarLoaderTests.cs` (6 callsites) goes away with the file deletion in P1a-31; the remaining 18 callsites are mechanical rewrites in two test files plus two production files.
