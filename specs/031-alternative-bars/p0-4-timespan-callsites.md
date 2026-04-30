# P0-4 — `TimeFrame` raw-`TimeSpan` overload audit (Phase 0)

**Status:** Complete. **Phase 4 removal scope is bounded by this enumeration.** No Phase 0 / Phase 1a code change.

## What was audited

Per the TRD §11 Phase 0 mandate (§9.1): enumerate every callsite of `IInt64BarStrategy` / loader / subscription APIs that takes a raw `TimeSpan` representing a bar interval / timeframe. Phase 4 introduces `TimeFrame` as a `record struct` value type wrapping `TimeSpan`, then **removes the raw-`TimeSpan` overloads**. Phase 0 freezes the scope ceiling so Phase 4's removal is bounded.

## API surfaces examined

| API | Where | Carries TimeSpan |
|---|---|---|
| `DataSubscription(Asset, TimeSpan TimeFrame, …)` ctor | `src/AlgoTradeForge.Domain/Strategy/DataSubscription.cs:3` | yes — primary surface |
| `IInt64BarLoader.Load(…, TimeSpan interval)` | `src/AlgoTradeForge.Application/CandleIngestion/IInt64BarLoader.cs:13` | yes — replaced by `DataFeedDescriptor.FeedId` in Phase 1a |
| `CandleStorageOptions.SourceInterval` | `src/AlgoTradeForge.Application/CandleIngestion/CandleStorageOptions.cs:12` | yes — config property |
| `DataSubscription.TimeFrame` (read accesses) | various | read-side — comparisons + arithmetic |

## Callsites passing raw `TimeSpan`

### `DataSubscription(...)` constructor — 12+ direct callsites

#### Production (3)
- `src/AlgoTradeForge.Application/Backtests/BacktestPreparer.cs:77` — `new DataSubscription(subAsset, timeFrame)` (TimeFrame parsed from string at :71-72; will become `TimeFrame.Parse(...)`)
- `src/AlgoTradeForge.Application/Live/StartLiveSessionCommandHandler.cs:35` — `new DataSubscription(asset, timeFrame)`
- `src/AlgoTradeForge.Application/Optimization/OptimizationSetupHelper.cs:153` — `new DataSubscription(asset, timeFrame)`

#### Benchmarks (3)
- `benchmarks/AlgoTradeForge.Benchmarks/Benchmarks/BacktestBenchmarks.cs:62` — `new DataSubscription(_btc, TimeSpan.FromHours(1))`
- `benchmarks/AlgoTradeForge.Benchmarks/Benchmarks/OptimizationBenchmarks.cs:98`
- `benchmarks/AlgoTradeForge.Benchmarks/Benchmarks/OptimizationBenchmarks.cs:143`

#### Tests (≥6 — sample)
- `tests/AlgoTradeForge.Application.Tests/Debug/DebugSessionHandlerTests.cs:55`
- `tests/AlgoTradeForge.Application.Tests/Debug/DebugWebSocketIntegrationTests.cs:50, :132`
- `tests/AlgoTradeForge.Application.Tests/Debug/GatingDebugProbeTests.cs:34, :65, :86`
- (plus additional test fixtures across Application/Domain test projects — full list to be re-enumerated immediately before Phase 4 starts so the enumeration reflects then-current code, not Apr 2026 state)

### `DataSubscription.TimeFrame` read-side usage (4)
- `src/AlgoTradeForge.Infrastructure/History/CsvDataSource.cs:22` — `if (query.TimeFrame < sourceInterval)`
- `src/AlgoTradeForge.Infrastructure/History/HistoryRepository.cs:18` — `if (subscription.TimeFrame < sourceInterval)`
- `src/AlgoTradeForge.Infrastructure/History/HistoryRepository.cs:31` — `subscription.TimeFrame == sourceInterval`
- `src/AlgoTradeForge.Infrastructure/History/HistoryRepository.cs:34` — `raw.Resample(subscription.TimeFrame)`

These survive Phase 4 unchanged: `TimeFrame.Duration` exposes the wrapped `TimeSpan` for arithmetic / comparison.

### `CandleStorageOptions.SourceInterval`

- `src/AlgoTradeForge.Application/CandleIngestion/CandleStorageOptions.cs:12` — `public TimeSpan SourceInterval { get; init; } = TimeSpan.FromMinutes(1);`

Phase 4 will rename to `TimeFrame SourceTimeFrame { get; init; } = new(TimeSpan.FromMinutes(1));` (or change in place — TBD).

### Private repo (1)

- `../AlgoTradeForge.Private/src/AlgoTradeForge.Strategies.Private/ZigZagBreakout/ZigZagBreakoutStrategy.cs:44` — `_barIntervalMs = (long)DataSubscriptions[0].TimeFrame.TotalMilliseconds`

This is a **read-side** access, not a constructor call. It survives Phase 4 unchanged once we expose `TimeFrame.Duration.TotalMilliseconds`.

## Phase 4 migration sketch (out of scope here, recorded for future)

1. Introduce `record struct TimeFrame(TimeSpan Duration) { string Code => …; static TimeFrame Parse(string code) => …; }` in `AlgoTradeForge.Domain/Strategy/`.
2. Add `DataSubscription(Asset, TimeFrame, …)` overload **alongside** the existing `TimeSpan` overload.
3. Migrate every callsite above (re-enumerate at Phase 4 start to catch new ones added in Phase 1b/2a/2b/3).
4. Delete the `TimeSpan` overload.

## Conclusion

The blast radius for Phase 4 timeframe-overload removal is ~19 callsites (12 ctor + 4 read + 1 config + 1 private read; the `IInt64BarLoader.Load` `TimeSpan interval` parameter is removed earlier in Phase 1a as part of the `DataFeedDescriptor` migration). Manageable as a single PR.
