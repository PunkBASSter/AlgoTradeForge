# Data Model: Production-Grade Backtest Engine

**Branch**: `003-backtest-engine` | **Date**: 2026-02-14

## Domain Entities

### Order (modified)

Extended with Stop/StopLimit trigger price, SL/TP fields. Existing fields unchanged.

```
Order (sealed class)
├── Id                : long              (required, existing)
├── Asset             : Asset             (required, existing)
├── Side              : OrderSide         (required, existing)
├── Type              : OrderType         (required, existing)
├── Quantity          : decimal           (required, existing)
├── LimitPrice        : decimal?          (existing — used by Limit and StopLimit)
├── StopPrice         : decimal?          (NEW — trigger price for Stop and StopLimit)
├── StopLossPrice     : decimal?          (NEW — auto-close at loss level)
├── TakeProfitLevels  : IReadOnlyList<TakeProfitLevel>?  (NEW — multiple TP with partial closure)
├── Status            : OrderStatus       (existing, default Pending)
└── SubmittedAt       : DateTimeOffset    (existing)
```

**Lifecycle (state transitions)**:
```
                    ┌──────────┐
                    │ Pending  │
                    └────┬─────┘
                         │
          ┌──────────────┼──────────────┐
          │              │              │
          ▼              ▼              ▼
    ┌──────────┐  ┌───────────┐  ┌──────────┐
    │ Triggered│  │  Filled   │  │ Rejected │
    │(Stop/SL) │  │           │  │          │
    └────┬─────┘  └───────────┘  └──────────┘
         │
    ┌────┼────┐
    ▼         ▼
┌────────┐ ┌──────────┐
│ Filled │ │Cancelled │
└────────┘ └──────────┘
```

- `Pending` → `Filled`: Market or Limit order fills directly
- `Pending` → `Triggered`: Stop/StopLimit trigger price reached
- `Triggered` → `Filled`: Limit price reached (StopLimit) or filled at trigger (Stop)
- `Pending`/`Triggered` → `Cancelled`: Strategy cancels the order
- `Pending` → `Rejected`: Insufficient funds at fill time

### OrderType (modified)

```
OrderType (enum)
├── Market     (existing)
├── Limit      (existing)
├── Stop       (NEW — trigger at StopPrice, fill at trigger + slippage)
└── StopLimit  (NEW — trigger at StopPrice, then becomes Limit at LimitPrice)
```

### OrderStatus (modified)

```
OrderStatus (enum)
├── Pending    (existing)
├── Filled     (existing)
├── Rejected   (existing)
├── Triggered  (NEW — Stop/StopLimit has been triggered, awaiting fill)
└── Cancelled  (NEW — strategy cancelled the order)
```

### TakeProfitLevel (new)

Defines a single take-profit target with the percentage of position to close.

```
TakeProfitLevel (readonly record struct)
├── Price             : decimal    (target price)
└── ClosurePercentage : decimal    (0.0–1.0, fraction of remaining position to close)
```

**Validation**: Sum of all `ClosurePercentage` values for an order MUST equal 1.0 (full position closure across all TP levels).

### ~~FillReason~~ (REMOVED)

> Deleted — redundant with `hitTpIndex` out parameter in `BarMatcher.EvaluateSlTp`. The engine infers SL vs TP from `hitTpIndex < 0` (SL) vs `hitTpIndex >= 0` (TP).

### Fill (unchanged)

```
Fill (sealed record)
├── OrderId    : long              (existing)
├── Asset      : Asset             (existing)
├── Timestamp  : DateTimeOffset    (existing)
├── Price      : decimal           (existing)
├── Quantity   : decimal           (existing)
├── Side       : OrderSide         (existing)
└── Commission : decimal           (existing)
```

### OrderQueue (new)

Engine-internal collection managing pending orders.

```
OrderQueue (sealed class)
├── Submit(order: Order)                     → void
├── Cancel(orderId: long)                    → bool
├── GetPendingForAsset(asset: Asset)         → IReadOnlyList<Order>
├── GetAll()                                 → IReadOnlyList<Order>
├── Remove(orderId: long)                    → void
└── Count                                    → int
```

**Invariants**:
- Orders returned by `GetPendingForAsset` are in submission order (FIFO)
- `Cancel` returns false if order not found or already filled
- Thread safety not required (single-threaded backtest loop)

### ~~BarResampler~~ → `TimeSeriesExtensions.Resample` (extension method)

> Moved from standalone static class to extension method on `TimeSeries<Int64Bar>` in `src/AlgoTradeForge.Domain/History/TimeSeriesExtensions.cs`.

```
TimeSeries<Int64Bar>.Resample(targetStep: TimeSpan) → TimeSeries<Int64Bar>
```

**Aggregation rules**:
- Open: first bar's Open in the group
- High: max High across all bars in the group
- Low: min Low across all bars in the group
- Close: last bar's Close in the group
- Volume: sum of all bars' Volume in the group
- Timestamp: first bar's timestamp in the group

**Validation**: `targetStep` MUST be an exact multiple of `source.Step`.

### IOrderContext (new)

Strategy-facing interface for order management during bar events.

```
IOrderContext (interface)
├── Submit(order: Order)             → long (returns order ID)
├── Cancel(orderId: long)            → bool
├── GetPendingOrders()               → IReadOnlyList<Order>
└── GetFills()                       → IReadOnlyList<Fill>
```

**Contract**: Available only during `OnBar` callback. The engine provides a concrete implementation that delegates to the `OrderQueue` and fill list.

### BacktestOptions (modified)

Extended with detailed execution logic flag.

```
BacktestOptions (sealed record)
├── InitialCash               : decimal        (existing)
├── Asset                     : Asset          (existing — retained for primary asset context)
├── StartTime                 : DateTimeOffset (existing)
├── EndTime                   : DateTimeOffset (existing)
├── CommissionPerTrade        : decimal        (existing)
├── SlippageTicks             : decimal        (existing — decimal for fractional tick slippage)
└── UseDetailedExecutionLogic : bool           (NEW, default false)
```

## Application Layer Entities

### IHistoryRepository (new)

```
IHistoryRepository (interface)
└── Load(subscription: DataSubscription, from: DateOnly, to: DateOnly)
    → TimeSeries<Int64Bar>
```

**Contract**: Loads bar data for the given subscription. If the stored timeframe is finer than requested (e.g., 1m stored but 5m requested), resamples automatically. Returns an empty `TimeSeries` if no data is available for the range.

## ~~Trading Module Entities~~ (DEFERRED)

> IOrderTracker, OrderTracker, TrackedPosition, and ClosedTrade have been **removed from current scope** for redesign. See future iteration.

## Entity Relationships

```
RunBacktestCommandHandler (Application layer)
 ├── uses → IHistoryRepository (loads data per subscription)
 │            └── wraps → IInt64BarLoader (existing CSV loader)
 │            └── uses → TimeSeries<Int64Bar>.Resample() (timeframe aggregation)
 ├── passes → TimeSeries<Int64Bar>[] + DataSubscription[] to BacktestEngine
 └── when UseDetailedExecutionLogic:
      └── loads SmallestInterval data per asset → Dictionary<Asset, TimeSeries<Int64Bar>>
          (passed as auxiliary data, NOT delivered to strategy via OnBar)

BacktestEngine (Domain layer — receives pre-loaded data, no IHistoryRepository dependency)
 ├── receives → TimeSeries<Int64Bar>[] + DataSubscription[] (from handler)
 ├── receives → Dictionary<Asset, TimeSeries<Int64Bar>>? (auxiliary lower-TF data, optional)
 ├── manages → OrderQueue (pending orders)
 │              └── contains → Order (with SL/TP fields)
 ├── uses → IBarMatcher (order fill simulation + SL/TP evaluation)
 │            ├── produces → Fill
 │            └── uses auxiliary lower-TF data for ambiguous SL/TP resolution
 ├── provides → IOrderContext (to strategy during OnBar)
 └── updates → Portfolio (after each fill)

IIntBarStrategy
 ├── declares → DataSubscription[] (subscriptions)
 ├── receives → Int64Bar + DataSubscription (per bar event)
 └── uses → IOrderContext (submit/cancel orders)
```
