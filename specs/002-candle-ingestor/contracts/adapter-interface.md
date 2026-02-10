# Data Adapter Interface Contract

**Version**: 1.0

## IDataAdapter

The primary interface for exchange-specific candle data fetching.

```csharp
namespace AlgoTradeForge.Domain.History;

public interface IDataAdapter
{
    IAsyncEnumerable<RawCandle> FetchCandlesAsync(
        string symbol,
        TimeSpan interval,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct);
}
```

## Contract Guarantees

### Caller expectations (orchestrator → adapter):
- `from` < `to`
- `interval` is a positive TimeSpan (e.g., 1 minute, 5 minutes, 1 hour)
- `symbol` is a valid exchange-native symbol (e.g., "BTCUSDT")
- `ct` should be respected for graceful cancellation

### Implementer guarantees (adapter → orchestrator):
- Returns candles in ascending timestamp order
- All timestamps are UTC (`DateTimeOffset` with offset +00:00)
- Handles API pagination internally (caller sees a continuous stream)
- Enforces exchange rate limits internally (caller does not need to throttle)
- Yields results as they arrive (streaming, not buffered)
- Empty enumerable if no data in range (does not throw)
- Throws on unrecoverable errors (IP ban, auth failure) after logging

## RawCandle

```csharp
namespace AlgoTradeForge.Domain.History;

public readonly record struct RawCandle(
    DateTimeOffset Timestamp,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);
```

## Registration

Adapters are registered as keyed DI services:

```csharp
services.AddKeyedSingleton<IDataAdapter, BinanceAdapter>("Binance");
```

Resolution by exchange name from asset configuration:

```csharp
var adapter = provider.GetRequiredKeyedService<IDataAdapter>(asset.Exchange);
```
