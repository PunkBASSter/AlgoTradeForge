# Quickstart: Production-Grade Backtest Engine

**Branch**: `003-backtest-engine` | **Date**: 2026-02-14

## Prerequisites

- .NET 10 SDK
- Git (on branch `003-backtest-engine`)
- Candle data in `Data/Candles/` (from `002-candle-ingestor`)

## Build

```bash
dotnet build AlgoTradeForge.slnx
```

## Run Tests

```bash
dotnet test AlgoTradeForge.slnx
```

## Write a Strategy

```csharp
public sealed class MyStrategy : StrategyBase<MyParams>
{
    public MyStrategy(MyParams parameters) : base(parameters) { }

    public override void OnBar(Int64Bar bar, DataSubscription subscription, IOrderContext orders)
    {
        // Strategy receives one bar at a time — live-like conditions
        // Use orders.Submit() to place orders
        // Use orders.GetFills() to observe executions
        // Use orders.Cancel() to cancel pending orders
    }
}

public sealed class MyParams : StrategyParamsBase
{
    public override IList<DataSubscription> DataSubscriptions { get; init; } =
    [
        new(Asset.Crypto("BTCUSDT", "Binance", 2), TimeSpan.FromMinutes(1)),
        new(Asset.Crypto("ETHUSDT", "Binance", 2), TimeSpan.FromMinutes(5))
    ];
}
```

## Order Types

| Type | Trigger | Fill |
|------|---------|------|
| Market | Immediate (next bar) | Next bar's Open + slippage |
| Limit | Bar price reaches LimitPrice | At LimitPrice |
| Stop | Bar price reaches StopPrice | At StopPrice + slippage |
| StopLimit | Bar price reaches StopPrice | Becomes Limit at LimitPrice |

## SL/TP on Orders

```csharp
orders.Submit(new Order
{
    Asset = subscription.Asset,
    Side = OrderSide.Buy,
    Type = OrderType.Stop,
    Quantity = 1.0m,
    StopPrice = bar.High + 100,         // trigger price
    StopLossPrice = bar.High - 500,     // auto-close at loss
    TakeProfitLevels =                  // multi-level profit taking
    [
        new TakeProfitLevel(bar.High + 1000, 0.5m),   // close 50% at +1000
        new TakeProfitLevel(bar.High + 2000, 0.5m)    // close 50% at +2000
    ]
});
```

## Key Architecture

```
Data Flow:
  CSV files → IHistoryRepository → TimeSeries<Int64Bar> (per subscription)
                                        ↓
  BacktestEngine (k-way merge of subscriptions, chronological order)
                                        ↓
  IIntBarStrategy.OnBar(bar, subscription, orderContext)
       ↓                                ↓
  Submit orders                    Process fills
       ↓                                ↓
  OrderQueue → BarMatcher → Fill → Portfolio update
```

## Project Layout

| Project | Role |
|---------|------|
| `AlgoTradeForge.Domain` | Engine, strategy interface, order model, bar matcher, resampler |
| `AlgoTradeForge.Application` | `IHistoryRepository` interface, backtest command/handler |
| `AlgoTradeForge.Infrastructure` | `HistoryRepository` implementation (wraps CSV loader + resampling) |
