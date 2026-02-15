# History Repository Contract

**Version**: 1.0

## IHistoryRepository

Abstraction for loading historical bar data for backtest subscriptions. Wraps existing `IInt64BarLoader` and adds timeframe resampling.

```csharp
namespace AlgoTradeForge.Application.Abstractions;

public interface IHistoryRepository
{
    TimeSeries<Int64Bar> Load(
        DataSubscription subscription,
        DateOnly from,
        DateOnly to);
}
```

## Contract Guarantees

### Caller expectations (engine → repository):
- `subscription` contains a valid `Asset` with `Exchange` and `Name` set
- `subscription.TimeFrame` is a positive `TimeSpan` (e.g., 1 minute, 5 minutes, 1 hour)
- `from` <= `to`
- `from` and `to` define an inclusive date range

### Implementer guarantees (repository → engine):
- Returns bars in ascending timestamp order
- All timestamps are UTC (`DateTimeOffset` with offset +00:00)
- If stored data has a finer resolution than `subscription.TimeFrame`, resamples automatically using standard OHLCV aggregation (first Open, max High, min Low, last Close, sum Volume)
- Returns an empty `TimeSeries<Int64Bar>` if no data is available for the range (does not throw)
- Handles missing monthly partition files gracefully (skips gaps)
- Resampling target interval MUST be an exact multiple of the source interval

## Resolution Logic

```
1. Extract exchange, symbol from subscription.Asset
2. Determine source interval (Asset.SmallestInterval, typically 1m)
3. Load raw bars via IInt64BarLoader for [from, to] range
4. If subscription.TimeFrame == source interval → return as-is
5. If subscription.TimeFrame > source interval → resample via BarResampler
6. If subscription.TimeFrame < source interval → error (cannot downsample)
```

## Registration

```csharp
services.AddSingleton<IHistoryRepository, HistoryRepository>();
```

The `HistoryRepository` implementation depends on:
- `IInt64BarLoader` (existing, for raw CSV loading)
- `IOptions<CandleIngestorOptions>` (for `DataRoot` configuration)
