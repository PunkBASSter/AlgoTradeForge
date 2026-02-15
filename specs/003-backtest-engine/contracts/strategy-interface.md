# Strategy Interface Contract

**Version**: 2.0 (replaces 1.0 from prototype)

## IIntBarStrategy

The primary interface for backtestable trading strategies. Receives bar events one at a time under live-like conditions.

```csharp
namespace AlgoTradeForge.Domain.Strategy;

public interface IIntBarStrategy
{
    IList<DataSubscription> DataSubscriptions { get; }

    void OnBar(Int64Bar bar, DataSubscription subscription, IOrderContext orders);
}
```

### Breaking changes from v1.0

- `OnBarComplete(TimeSeries<Int64Bar> context)` replaced by `OnBar(Int64Bar, DataSubscription, IOrderContext)`
- Strategy no longer receives full time series — only the single completed bar
- Strategy no longer returns a value; orders are submitted via `IOrderContext`

## IOrderContext

Injected into the strategy during each `OnBar` call. Provides order submission, cancellation, and query capabilities.

```csharp
namespace AlgoTradeForge.Domain.Strategy;

public interface IOrderContext
{
    long Submit(Order order);
    bool Cancel(long orderId);
    IReadOnlyList<Order> GetPendingOrders();
    IReadOnlyList<Fill> GetFills();
}
```

## Contract Guarantees

### Caller expectations (engine → strategy):
- `OnBar` is called once per completed bar per subscription, in global chronological order
- The `bar` parameter contains only the newly completed bar's OHLCV data
- The `subscription` parameter identifies which data subscription this bar belongs to
- The `orders` parameter is valid only for the duration of the `OnBar` call
- The strategy MUST NOT store or use `IOrderContext` outside of `OnBar`

### Implementer guarantees (strategy → engine):
- Strategy MUST NOT perform I/O (file, network, database) during `OnBar`
- Strategy MUST NOT block or perform long-running operations
- Strategy MAY accumulate its own bar history internally if needed
- Strategy MAY submit zero or more orders per `OnBar` call
- Strategy MAY cancel previously submitted pending orders
- Strategy MAY query pending orders and fills for decision-making

## StrategyBase<TParams>

Updated abstract base class with default `DataSubscriptions` from params.

```csharp
namespace AlgoTradeForge.Domain.Strategy;

public abstract class StrategyBase<TParams> : IIntBarStrategy
    where TParams : StrategyParamsBase
{
    protected TParams Params { get; }

    protected StrategyBase(TParams parameters) => Params = parameters;

    public IList<DataSubscription> DataSubscriptions => Params.DataSubscriptions;

    public abstract void OnBar(Int64Bar bar, DataSubscription subscription, IOrderContext orders);
}
```

## Deleted Types

- `StrategyAction` — dead code, replaced by `IOrderContext.Submit(Order)`
