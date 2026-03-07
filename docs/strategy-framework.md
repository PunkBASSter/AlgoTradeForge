# Strategy Framework — Development Plan

**Goal:** Decompose strategy logic into reusable modules with exact responsibilities. Modules serve as composable building blocks so that new strategies require only unique entry logic while sharing position sizing, trailing stops, regime detection, and signal scoring. Must remain compatible with existing `BacktestEngine` (stateless, concurrent), `[Optimizable]` parameter sweep, `IEventBus` debug export, and multi-subscription `DataSubscription` architecture.

**Existing foundation (as of current codebase):**
- `StrategyBase<TParams>` — Template Method with virtual `OnBarStart`, `OnBarComplete`, `OnInit`, `OnTrade`
- `IStrategyModule` / `IStrategyModule<TParams>` — marker interfaces
- `ModuleParamsBase` — base for module-specific optimizable params
- `[ModuleKey]` attribute for module registration/identification
- `[Optimizable]` attribute for parameter optimization ranges
- `AtrVolatilityFilterModule` — first concrete module (Initialize + IsAllowed pattern)
- `IIndicatorFactory` / `Int64IndicatorBase` / `IndicatorBuffer<long>` / `RingBuffer<T>` — indicator pipeline
- `IOrderContext` — Submit/Cancel/GetPendingOrders/GetFills
- `SignalEvent` / `RiskEvent` — event bus integration for debug and logging
- `DeltaZigZag` indicator — the only existing advanced indicator beyond ATR
- `BuyAndHoldStrategy` — reference strategy showing StrategyBase usage

---

## Module Architecture

### Module Lifecycle

All modules follow the same lifecycle contract, driven by `StrategyBase`:

```
OnInit()           → module.Initialize(factory, subscription)
OnBarStart(bar)    → module.Update(bar)  [optional, for modules needing open-price data]
OnBarComplete(bar) → module.Evaluate(bar, context)  → returns typed result
OnTrade(fill)      → module.OnFill(fill) [optional, for trade-aware modules]
```

`StrategyBase<TParams>` orchestrates this via Template Method: the base class iterates registered modules at each lifecycle point, descendants override `OnSignal()` / `OnFilter()` etc. to compose results into trading decisions.

### Module Communication: StrategyContext

A lightweight context object is passed through all modules on each bar. It holds:
- Current bar data per subscription (latest bars, keyed by DataSubscription)
- Indicator values (accessible via module's own indicator references)
- Current position state (from Portfolio / IOrderContext)
- Regime classification (written by RegimeDetector, read by others)
- Custom key-value store (`Dictionary<string, object>`) for module-to-module data passing

This avoids tight coupling between modules. A module publishes data to context; another reads it. No direct references between modules.

### Return Types: Signal Strength Convention

Filter and Exit modules return `int` in the range **[-100, +100]**:
- **+100** = maximum bullish conviction (Filter: strongly allow longs / Exit: strongly hold)
- **-100** = maximum bearish conviction (Filter: strongly allow shorts / Exit: strongly close)
- **0** = neutral / no opinion
- Intermediate values allow weighted composition in the Signal Scoring module

This integer range is optimizer-friendly (no floating point), composable (weighted sum), and debuggable (maps naturally to percentage strength in SignalEvent).

---

## Module Catalog

### Priority 1 — Foundational (used by nearly every strategy)

#### 1.1 TradeRegistry Module
**Reusability: 10/10** — Every non-trivial strategy needs it.

Tracks order groups, fills, positions, SL/TP belonging to the current strategy. Provides a convenient API to create an order group owned by the strategy and track it. Gives virtual isolated order management for strategies trading on the same account.

**Responsibilities:**
- Create and track OrderGroups (entry + SL + TP1..TPn as a unit)
- Map received order IDs / fills to their OrderGroup
- Track per-group state: pending, partially filled, fully filled, closed
- Proxy `OnTrade(fill, order)` events to the correct group handler
- Emit `OrderGroupEvent` to event bus with group ID for debug visualization
- Persist group state for live session reconnection (serialize to JSON)
- Expose: `ActiveGroups`, `GetGroup(id)`, `CloseGroup(id)`, `IsFlat`
- Concurrent groups limit (configurable): prevent over-exposure

**Compatibility notes:**
- OrderGroup ID propagated as optional metadata on `Order` — does NOT affect BacktestEngine fill logic
- Debug frontend connects open/close prices with dashed line using group ID (per `order-groups.md`)
- Works with existing `IOrderContext.Submit()` — TradeRegistry wraps it, not replaces it

**Params:**
- `MaxConcurrentGroups` — `[Optimizable(Min = 1, Max = 10, Step = 1)]`
- `AllowPyramiding` — bool

#### 1.2 MoneyManagement (Position Sizing) Module
**Reusability: 10/10** — Every strategy needs position sizing.

Evaluates risk per deal and calculates position size. Three methods:

| Method | Formula | When to use |
|---|---|---|
| Fixed Fractional | `Equity × Risk% / (Entry − Stop)` | Default. Requires a stop price. |
| ATR-Based Vol Target | `Equity × VolTarget / (ATR × Multiplier)` | When ATR is available. |
| Half-Kelly | `0.5 × (WinRate − (1−WinRate)/PayoffRatio)` | After sufficient trade history. |

**Responsibilities:**
- Accept proposed entry price and stop-loss price → return position size (decimal quantity)
- Respect `Asset.MinOrderQuantity` / `MaxOrderQuantity` / `QuantityStepSize` / `RoundQuantityDown()`
- Read equity from `IOrderContext.Cash` + portfolio positions
- Optionally adjust size based on regime (reduce by 50% in high-vol regime if configured)
- Emit `RiskEvent` to event bus with check results

**Params:**
- `Method` — enum {FixedFractional, AtrVolTarget, HalfKelly}
- `RiskPercent` — `[Optimizable(Min = 0.5, Max = 5.0, Step = 0.5)]` — % of equity risked per trade
- `VolTarget` — `[Optimizable(Min = 0.05, Max = 0.30, Step = 0.05)]` — annualized vol target
- `VolRegimeReduction` — `[Optimizable(Min = 0.0, Max = 0.75, Step = 0.25)]` — sizing reduction in high-vol

#### 1.3 Trailing Stop Module
**Reusability: 9/10** — Used by all trend-following and most mean-reversion strategies.

Three trailing stop variants, all sharing the constraint that the stop only moves in the favorable direction:

| Variant | Logic | Default Params |
|---|---|---|
| ATR Trailing | `stop = HighestHigh − N×ATR` (longs) | N = 3.0, ATR period = 14 |
| Chandelier Exit | `stop = HighestHigh(22) − 3×ATR(22)` | Period = 22, mult = 3.0 |
| Donchian Exit | `stop = LowestLow(N)` (longs) | N = 10 |

**Responsibilities:**
- Initialize with variant selection and params
- On each bar: compute new stop level, ratchet if favorable
- Expose `CurrentStop` property for strategy to read
- Emit stop-level updates to event bus for chart overlay in debug
- Compatible with TradeRegistry: can update SL on active OrderGroup

**Params:**
- `Variant` — enum {AtrTrailing, Chandelier, Donchian}
- `Multiplier` — `[Optimizable(Min = 1.5, Max = 6.0, Step = 0.5)]`
- `Period` — `[Optimizable(Min = 5, Max = 50, Step = 1)]`

#### 1.4 Filter Module Interface Standardization
**Reusability: 10/10** — Already partially exists (`AtrVolatilityFilterModule`). Standardize the interface.

Rename / formalize the contract. All filters implement:

```csharp
public interface IFilterModule : IStrategyModule
{
    void Initialize(IIndicatorFactory factory, DataSubscription subscription);
    int Evaluate(Int64Bar bar, OrderSide proposedSide); // [-100, +100]
}
```

Existing `AtrVolatilityFilterModule.IsAllowed()` refactored to return `int` (100 if allowed, 0 if blocked). This enables weighted composition when multiple filters are stacked.

**Concrete filters to implement (in priority order):**

1. **AtrVolatilityFilter** — already exists, adapt return type
2. **SessionTimeFilter** — allow/block by UTC hour ranges and day-of-week
3. **RegimeFilter** — delegate to RegimeDetector, block counter-regime trades
4. **MultiTimeframeAlignmentFilter** — require HTF trend alignment before LTF entry
5. **CorrelationRegimeFilter** — for pairs: block when cointegration breaks down

### Priority 2 — Regime & Analytics (enable adaptive strategies)

#### 2.1 Regime Detection Module
**Reusability: 8/10** — Used by all hybrid/adaptive strategies. Also valuable as a standalone filter.

Classifies current market state every N bars:

```csharp
public enum MarketRegime { TrendingBull, TrendingBear, Ranging, Volatile, Quiet }
```

**Classification logic (composite):**
- ADX > threshold → Trending (DI+ > DI- → Bull, else Bear)
- ATR / avg(ATR, 50) > 1.5 → Volatile
- ATR / avg(ATR, 50) < 0.8 AND ADX < 20 → Quiet
- Else → Ranging
- Hysteresis: minimum 3 bars in proposed state before switching

**Optional enrichment (computed but not required for classification):**
- Hurst exponent (rolling window 100-150 bars) — H > 0.55 confirms trending, H < 0.45 confirms mean-reverting
- Autocorrelation at lag-1 — positive confirms momentum, negative confirms reversion

**Params:**
- `AdxPeriod` — `[Optimizable(Min = 7, Max = 28, Step = 1)]`
- `AdxThreshold` — `[Optimizable(Min = 15, Max = 35, Step = 5)]`
- `VolatilityRatioHigh` — `[Optimizable(Min = 1.2, Max = 2.0, Step = 0.1)]`
- `VolatilityRatioLow` — `[Optimizable(Min = 0.5, Max = 0.9, Step = 0.1)]`
- `HysteresisMinBars` — `[Optimizable(Min = 1, Max = 10, Step = 1)]`

Publishes `MarketRegime` to StrategyContext on each evaluation. Other modules read it.

#### 2.2 Volatility Estimation Module
**Reusability: 8/10** — Feeds into position sizing, regime detection, stop-loss width, and squeeze detection.

Implements OHLCV-based volatility estimators — significantly more efficient than close-to-close:

| Estimator | Handles Drift | Handles Gaps | Efficiency vs C2C |
|---|---|---|---|
| Parkinson | No | No | 5.2× |
| Garman-Klass | No | No | 7.4× |
| Rogers-Satchell | Yes | No | 6× |
| Yang-Zhang | Yes | Yes | 14× |

**Default:** Yang-Zhang for assets with session gaps (stocks/futures), Garman-Klass for continuous markets (crypto).

Implemented as `Int64IndicatorBase` subclass(es), output buffer in integer volatility units. Rolling window of 10-20 bars. 

**Params:**
- `Estimator` — enum {Parkinson, GarmanKlass, RogersSatchell, YangZhang}
- `Period` — `[Optimizable(Min = 5, Max = 30, Step = 1)]`

#### 2.3 Signal Scoring (Composite Signal) Module
**Reusability: 7/10** — Used by all multi-factor and hybrid strategies.

Takes N indicator/module outputs (each normalized to [-100, +100]), applies configurable weights, outputs composite score. Entry when composite exceeds threshold; exit when drops below.

```csharp
public interface ISignalContributor
{
    string Name { get; }
    int Evaluate(Int64Bar bar); // [-100, +100]
}
```

**Scoring:**
```
CompositeScore = Σ(weight_i × contributor_i.Evaluate(bar)) / Σ(weight_i)
```

Weights are the primary optimization parameters. The strategy enters when `CompositeScore > EntryThreshold` and exits when `CompositeScore < ExitThreshold`.

**Params:**
- `Weights` — array of doubles, one per contributor `[Optimizable]`
- `EntryThreshold` — `[Optimizable(Min = 20, Max = 80, Step = 5)]`
- `ExitThreshold` — `[Optimizable(Min = -20, Max = 40, Step = 5)]`

#### 2.4 Exit Module
**Reusability: 7/10** — Complements trailing stops with rule-based exits.

Evaluates whether a position should be closed based on non-price-stop criteria:

| Exit Rule | Logic |
|---|---|
| Time-based | Close after N bars since entry |
| End-of-session | Close at configurable UTC hour (e.g., market close) |
| End-of-week | Close on Friday before weekend (forex/crypto risk) |
| Regime change | Close when regime flips (e.g., exit trend position when Ranging detected) |
| Signal reversal | Close when composite signal flips sign |
| Profit target | Close when unrealized P&L exceeds N×ATR or fixed percentage |

Returns `int [-100, +100]`: -100 = close immediately, 0 = no opinion, +100 = hold strongly. Multiple exit rules are evaluated; the most extreme value wins.

**Params:**
- `MaxHoldBars` — `[Optimizable(Min = 5, Max = 200, Step = 5)]`
- `SessionCloseHourUtc` — int (0-23)
- `CloseOnRegimeChange` — bool
- `ProfitTargetAtrMultiple` — `[Optimizable(Min = 1.0, Max = 10.0, Step = 0.5)]`

### Priority 3 — Multi-Asset & Statistical (enable pairs trading and rotation)

#### 3.1 Cross-Asset Correlation / Cointegration Module
**Reusability: 6/10** — Required for pairs trading, lead-lag, and correlation regime strategies.

Computes rolling statistics between two asset price series:

**Outputs:**
- Rolling Pearson correlation (configurable window)
- OLS hedge ratio (β): `spread = Price_A − β × Price_B`
- Spread z-score: `(spread − μ) / σ`
- ADF test p-value (Engle-Granger cointegration test)
- Half-life of mean reversion (from OU model: `−ln(2)/λ`)

**Implementation:** Requires access to two `DataSubscription` series simultaneously. The module registers for both subscriptions in Initialize and maintains internal rolling buffers. Publishes z-score, hedge ratio, and cointegration status to StrategyContext.

**Params:**
- `HedgeRatioWindow` — `[Optimizable(Min = 30, Max = 252, Step = 10)]`
- `ZScoreWindow` — `[Optimizable(Min = 15, Max = 60, Step = 5)]`
- `ZScoreEntryThreshold` — `[Optimizable(Min = 1.5, Max = 3.0, Step = 0.25)]`
- `ZScoreExitThreshold` — `[Optimizable(Min = 0.0, Max = 1.0, Step = 0.25)]`
- `CointegrationPValueMax` — 0.05 (fixed — if exceeded, flatten)

**Compatibility:** Multi-subscription by design; uses existing `DataSubscription[]` array in BacktestEngine. Each subscription feeds its bars independently; the module aligns by timestamp internally.

#### 3.2 Multi-Timeframe Resampler Module
**Reusability: 6/10** — Used by any strategy applying HTF confirmation.

Resamples lower-timeframe bars to higher-timeframe bars using **right-edge labeling** (critical for preventing look-ahead bias: HTF bar only completes when its period closes). Forward-fills last completed HTF values to all LTF bars within the incomplete period.

**Implementation:** This is largely handled by the existing multi-subscription architecture — a strategy can subscribe to both H1 and H4 for the same asset. The module's value is in providing a clean API for:
- Querying the latest completed HTF bar and indicators
- Confirming HTF trend direction (SMA slope, ADX, price vs MA)
- Providing alignment score [-100, +100] to the filter/scoring pipeline

Uses existing `DataSubscription` mechanism: strategy declares e.g. `(BTCUSDT, 1H, exportable=true)` and `(BTCUSDT, 4H, exportable=false)`.

#### 3.3 Statistical Tests Module
**Reusability: 5/10** — Required for pairs trading validation, regime detection enrichment.

Implements:
- **ADF test** (Augmented Dickey-Fuller) — stationarity test, gatekeeper for pairs trading
- **Rolling autocorrelation** at configurable lags
- **Hurst exponent** (rescaled range or variance method)
- **Jarque-Bera** normality test

All operate on `long[]` or `double[]` arrays. Pure math functions, no indicator state — can be called on-demand by other modules.

**Note:** Hurst exponent computation is CPU-intensive (O(N×log(N)) for each evaluation). Use sparingly — compute every 10-20 bars, cache result. The `RingBuffer<T>` is ideal for maintaining the rolling window.

### Priority 4 — Session/Calendar & Volume Profile (enable time-based and market-structure strategies)

#### 4.1 Session/Time Filter Module
**Reusability: 6/10** — Used by ORB, session overlap, and any session-aware strategy.

Defines trading windows by UTC hour ranges and day-of-week. On hourly bars, this is a timestamp comparison against `Int64Bar.TimestampMs`.

**Params:**
- `AllowedStartHourUtc` — int (0-23)
- `AllowedEndHourUtc` — int (0-23)
- `AllowedDays` — flags enum {Mon, Tue, Wed, Thu, Fri, Sat, Sun}
- `SessionPreset` — enum {Custom, LondonOpen, NYOpen, LondonNYOverlap, AsianSession}

#### 4.2 Volume Profile Module
**Reusability: 5/10** — Used by market-structure strategies.

Approximates volume profile from OHLCV bars: distributes each bar's volume across its price range, weighted toward close. Over N bars, builds a histogram identifying:
- **Point of Control (POC)** — price level with highest accumulated volume
- **Value Area High/Low** — boundaries containing 70% of volume
- **Current price position** relative to value area (above / inside / below)

Implemented as `Int64IndicatorBase` with multiple output buffers: `POC`, `VAH`, `VAL`.

**Params:**
- `LookbackBars` — `[Optimizable(Min = 20, Max = 200, Step = 10)]`
- `PriceBinCount` — `[Optimizable(Min = 20, Max = 100, Step = 10)]` — resolution of histogram
- `ValueAreaPercent` — 70 (fixed)

---

## New Indicators Required

Indicators follow the existing `Int64IndicatorBase` pattern: stateful, incremental `Compute()`, `IndicatorBuffer<long>` outputs, `[Optimizable]` params where applicable.

### Priority 1 — Enable core strategies

| Indicator | Buffers | Needed By | Complexity |
|---|---|---|---|
| **SMA** | Value | MA crossovers, regime, filters | Low |
| **EMA** | Value | MA crossovers, MACD, Keltner | Low |
| **RSI** | Value | Mean-reversion, scoring | Medium |
| **ADX** | ADX, PlusDI, MinusDI | Regime detection, trend filter | Medium |
| **BollingerBands** | Upper, Middle, Lower, Width | Mean-reversion, squeeze | Medium |
| **MACD** | MACD, Signal, Histogram | Squeeze confirmation, momentum | Medium |
| **DonchianChannel** | Upper, Lower, Middle | Turtle/breakout, trailing stop | Low |
| **OBV** (On-Balance Volume) | Value | Volume divergence, confirmation | Low |
| **KeltnerChannel** | Upper, Lower, Middle | Squeeze detection | Medium |
| **VWAP** | Value, UpperBand, LowerBand | VWAP reversion | Medium |

### Priority 2 — Enable advanced strategies

| Indicator | Buffers | Needed By | Complexity |
|---|---|---|---|
| **Stochastic** | K, D | Momentum, mean-reversion | Medium |
| **ParkinsonVolatility** | Value | Volatility estimation | Low |
| **GarmanKlassVolatility** | Value | Volatility estimation | Low |
| **YangZhangVolatility** | Value | Volatility estimation (default) | Medium |
| **LinearRegressionChannel** | Value, Upper, Lower, Slope | Stat strategies, regime | Medium |
| **HurstExponent** | Value | Regime detection (enrichment) | High |
| **RollingCorrelation** | Value | Multi-asset module | Medium |

### Priority 3 — Specialized

| Indicator | Buffers | Needed By | Complexity |
|---|---|---|---|
| **VolumeProfile** | POC, VAH, VAL | Market structure strategies | High |
| **AccumulationDistribution** | Value | Volume analysis | Low |
| **ChandeMomentumOscillator** | Value | Momentum scoring | Medium |
| **WilliamsR** | Value | Mean-reversion scoring | Low |

**Implementation note:** All indicators use `long` arithmetic consistent with `Int64Bar`. Price-derived indicators output in the same integer-scaled units. Ratio indicators (RSI, Stochastic, etc.) scale to 0-10000 range (equivalent to 0.00-100.00 with 2 decimal places).

---

## Strategy Implementation Roadmap

Strategies are grouped into milestones. Each milestone exercises a specific set of modules, validating them before the next milestone adds complexity.

### Milestone 1: Mean-Reversion (validates: MoneyManagement, Filter, Exit, indicators)
**Timeline: after Priority 1 modules + SMA/EMA/RSI/BollingerBands indicators**

#### Strategy: RSI(2) Mean-Reversion
- **Type:** Single-asset, mean-reversion
- **Logic:** RSI(2) < 10 → long entry (next bar open); RSI(2) > 90 → short entry. Exit when RSI crosses 50, or time-stop at N bars.
- **Subscriptions:** 1 asset, 1 timeframe (H1 or H4)
- **Modules used:** MoneyManagement (Fixed Fractional), AtrVolatilityFilter, Exit (time-based + signal reversal)
- **Indicators:** RSI(2), SMA(200) as trend filter
- **Optimization params:** RSI oversold/overbought thresholds, time-stop bars, risk percent
- **Asset classes:** Equity indices (SPY, QQQ), forex majors. Weaker on crypto.

#### Strategy: Bollinger Band Reversion
- **Type:** Single-asset, mean-reversion
- **Logic:** Close below lower BB → long; close above upper BB → short. Require candle close back inside bands (confirmation). Exit at middle band (SMA).
- **Subscriptions:** 1 asset, 1 timeframe
- **Modules used:** MoneyManagement, AtrVolatilityFilter, TrailingStop (optional — ATR trailing)
- **Indicators:** BollingerBands(20, 2.0), SMA(200), ATR(14)
- **Optimization params:** BB period, BB std dev, SMA trend filter period
- **Asset classes:** All. Tighten bands to 1.5σ on crypto.

### Milestone 2: Trend-Following (validates: TrailingStop, RegimeDetection, MTF alignment)
**Timeline: after Priority 2 modules + ADX/MACD/DonchianChannel indicators**

#### Strategy: Donchian Breakout with Regime Filter (Modern Turtle)
- **Type:** Single-asset, trend-following
- **Logic:** Enter long on 20-bar highest high breakout; short on 20-bar lowest low. Exit on 10-bar opposite breakout OR trailing stop. Only trade when RegimeDetector says Trending.
- **Subscriptions:** 1 asset, 2 timeframes (H1 for signals, H4 for regime confirmation)
- **Modules used:** TrailingStop (ATR trailing), RegimeDetection, MoneyManagement (ATR vol target), TradeRegistry (pyramiding up to 4 units)
- **Indicators:** DonchianChannel(20), DonchianChannel(10) for exit, ATR(20), ADX(14)
- **Optimization params:** Breakout period, exit period, ATR trailing multiplier, max pyramid units
- **Asset classes:** Crypto, commodities. Strong in trending markets.

#### Strategy: EMA Crossover + ADX + MTF Alignment
- **Type:** Single-asset, trend-following
- **Logic:** 8/21 EMA crossover for entry. ADX(14) > 25 as trend confirmation. 4H SMA(50) direction must agree.
- **Subscriptions:** 1 asset, 2 timeframes (H1 + H4)
- **Modules used:** MultiTimeframeAlignment Filter, RegimeDetection, TrailingStop (Chandelier), MoneyManagement
- **Indicators:** EMA(8), EMA(21), SMA(50) on H4, ADX(14)
- **Optimization params:** Fast/slow EMA periods, ADX threshold, trailing stop params
- **Asset classes:** Forex, crypto. Avoids equities during range-bound periods.

### Milestone 3: Volatility-Based (validates: Volatility Estimation, SignalScoring)
**Timeline: after Volatility indicators + KeltnerChannel**

#### Strategy: Bollinger-Keltner Squeeze Breakout
- **Type:** Single-asset, volatility breakout
- **Logic:** Squeeze detected when BB inner bounds are inside Keltner Channel. Squeeze fires when BB expands back outside KC. Enter in direction of MACD histogram on fire.
- **Subscriptions:** 1 asset, 1 timeframe
- **Modules used:** SignalScoring (MACD direction + squeeze confirmation), TrailingStop (ATR), MoneyManagement, RegimeDetection (confirm Quiet→Volatile transition)
- **Indicators:** BollingerBands(20, 2.0), KeltnerChannel(20, 1.5), MACD(12, 26, 9), ATR(14)
- **Optimization params:** BB/KC periods, KC ATR multiplier, MACD params
- **Asset classes:** All. Especially effective on crypto.

### Milestone 4: Multi-Asset (validates: Cross-Asset module, Statistical Tests)
**Timeline: after Priority 3 modules + RollingCorrelation**

#### Strategy: Pairs Trading (Cointegration-Based)
- **Type:** Multi-asset, relative value, mean-reversion on spread
- **Logic:** Compute spread z-score between two cointegrated assets. Long spread when z < -2.0; short when z > +2.0. Exit when z crosses ±0.5. Flatten if ADF p-value > 0.05.
- **Subscriptions:** 2 assets, same timeframe (H1)
- **Modules used:** CrossAssetCorrelation (z-score, hedge ratio, ADF), MoneyManagement, Exit (cointegration breakdown → immediate flatten)
- **Indicators:** RollingCorrelation, custom spread/z-score from module
- **Optimization params:** Hedge ratio window, z-score lookback, entry/exit thresholds
- **Asset classes:** Crypto pairs (BTC/ETH, SOL/AVAX), equity pairs (KO/PEP), forex (AUDUSD/NZDUSD)

#### Strategy: Cross-Sectional Momentum Rotation
- **Type:** Multi-asset, momentum, rotation
- **Logic:** Rank N assets by trailing return over lookback period. Go long top K. Rebalance every R bars. Absolute momentum filter: only hold if return > 0.
- **Subscriptions:** N assets (5-30), same timeframe (H4 or D1)
- **Modules used:** MoneyManagement, Exit (rebalance timer), SessionTimeFilter (rebalance day)
- **Indicators:** SMA(lookback) for return calculation, ATR for volatility ranking
- **Optimization params:** Lookback period, top K count, rebalance frequency, absolute momentum threshold
- **Asset classes:** Crypto universe (top 20 by market cap), sector ETFs, forex basket

### Milestone 5: Hybrid / Regime-Adaptive (validates: full module composition)
**Timeline: after all modules operational**

#### Strategy: Regime-Switching Meta-Strategy
- **Type:** Single-asset, adaptive, multi-strategy
- **Logic:** RegimeDetector classifies market. Strategy router activates:
  - Trending → Donchian breakout / EMA crossover
  - Ranging → Bollinger/RSI mean-reversion
  - Quiet → Watch for squeeze breakout
  - Volatile → Reduce sizing or no-trade
- **Subscriptions:** 1 asset, 2 timeframes
- **Modules used:** ALL modules compose here — this is the integration test of the framework
- **Key challenge:** Strategy switching must handle open positions from the previous regime gracefully (don't abandon a profitable trend position because regime flipped for 3 bars — hysteresis matters)

#### Strategy: Multi-Factor Composite Scorer
- **Type:** Single-asset, multi-factor
- **Logic:** 5+ signal contributors each return [-100, +100]. Weighted sum via SignalScoring module. Enter when composite > threshold. Exit when composite < exit threshold.
- Contributors: trend alignment, momentum (RSI), volume confirmation, volatility state, multi-TF alignment
- **Modules used:** SignalScoring, all filters, MoneyManagement, TrailingStop
- **Optimization params:** 5+ weights, entry/exit thresholds (this is where walk-forward validation is essential to prevent overfitting the weight space)

---

## Implementation Order — Prioritized Steps

### Phase 1: Core Module Infrastructure (est. 2-3 weeks)

| Step | Task | Dependency |
|---|---|---|
| 1.1 | Formalize `IFilterModule` interface with `int Evaluate()` return type. Refactor `AtrVolatilityFilterModule` to conform. | None |
| 1.2 | Implement `StrategyContext` — lightweight per-bar context object with key-value store, regime slot, position summary. Passed through module pipeline. | None |
| 1.3 | Enhance `StrategyBase<TParams>` with module registration and lifecycle orchestration. Add `RegisterModule()`, iterate modules at OnInit/OnBarStart/OnBarComplete/OnTrade. Template Method exposes `OnSignal()`, `OnFilter()` inner methods. | 1.1, 1.2 |
| 1.4 | Implement core indicators: **SMA, EMA, RSI, BollingerBands, DonchianChannel, OBV** as `Int64IndicatorBase` subclasses. All use `IndicatorBuffer<long>`. Unit test each against known values. | None (parallel) |
| 1.5 | Implement **MoneyManagement module** — Fixed Fractional method first (simplest, most universal). Wire to StrategyContext for equity reading. | 1.2 |
| 1.6 | Implement **TradeRegistry module** — OrderGroup creation, fill tracking, group state management. Emit `OrderGroupEvent` to IEventBus. | 1.3 |
| 1.7 | Implement **TrailingStop module** — ATR trailing variant first. Ratchet logic, expose CurrentStop. | ATR indicator exists |

### Phase 2: First Strategies + Exit/Filter Modules (est. 2-3 weeks)

| Step | Task | Dependency |
|---|---|---|
| 2.1 | Implement **Exit module** — time-based exit first (MaxHoldBars), then profit target. | 1.3 |
| 2.2 | Implement **RSI(2) Mean-Reversion strategy** — validates MoneyManagement, Filter, Exit, RSI indicator. First real strategy using the module framework. | Phase 1 |
| 2.3 | Implement **Bollinger Band Reversion strategy** | Phase 1 |
| 2.4 | Validate module parameter optimization — ensure `[Optimizable]` on module params flows through `CartesianProductGenerator` and optimizer correctly. Test with brute-force optimization of RSI(2) strategy. | 2.2 |
| 2.5 | Validate debug export — ensure module events (signals, risk checks, stop levels) appear in JSONL event log and are consumable by debug frontend. | 2.2 |

### Phase 3: Regime Detection + Trend Strategies (est. 2-3 weeks)

| Step | Task | Dependency |
|---|---|---|
| 3.1 | Implement **ADX indicator** (with +DI/-DI buffers) and **MACD indicator** | None (parallel) |
| 3.2 | Implement **Regime Detection module** — ADX + volatility ratio classifier with hysteresis. | 3.1, ATR |
| 3.3 | Implement **SessionTimeFilter** and **RegimeFilter** (delegates to RegimeDetector) | 3.2, 1.1 |
| 3.4 | Implement **KeltnerChannel indicator** | EMA + ATR |
| 3.5 | Implement **Donchian Breakout strategy (Modern Turtle)** with regime filter | 3.2, DonchianChannel |
| 3.6 | Implement **EMA Crossover + ADX + MTF strategy** | 3.1, EMA, MultiTF setup |
| 3.7 | Implement **Bollinger-Keltner Squeeze strategy** | 3.4, BollingerBands, MACD |

### Phase 4: Volatility + Signal Scoring (est. 1-2 weeks)

| Step | Task | Dependency |
|---|---|---|
| 4.1 | Implement **volatility estimator indicators** — GarmanKlass and YangZhang | None |
| 4.2 | Implement **Volatility Estimation module** wrapper | 4.1 |
| 4.3 | Implement **Signal Scoring module** — weighted composite with `ISignalContributor` interface | 1.3 |
| 4.4 | Extend MoneyManagement with AtrVolTarget and HalfKelly methods | 4.2 |

### Phase 5: Multi-Asset + Statistical (est. 2-3 weeks)

| Step | Task | Dependency |
|---|---|---|
| 5.1 | Implement **RollingCorrelation indicator** | None |
| 5.2 | Implement **Statistical Tests module** — ADF test, Hurst exponent, autocorrelation | None |
| 5.3 | Implement **CrossAssetCorrelation module** — hedge ratio, z-score, cointegration check | 5.1, 5.2 |
| 5.4 | Implement **Pairs Trading strategy** | 5.3 |
| 5.5 | Implement **Cross-Sectional Momentum Rotation strategy** | Phase 1 modules + N subscriptions |

### Phase 6: Hybrid + Integration (est. 1-2 weeks)

| Step | Task | Dependency |
|---|---|---|
| 6.1 | Implement **Regime-Switching Meta-Strategy** | All modules |
| 6.2 | Implement **Multi-Factor Composite Scorer strategy** | SignalScoring + all filters |
| 6.3 | Walk-forward validation of all strategies — verify parameters are stable across windows | Optimization infrastructure |

---

## Compatibility Checklist

All modules and strategies must satisfy:

- [ ] **Thread safety:** Modules hold no shared mutable state between parallel optimization runs. Each BacktestEngine.Run() gets fresh module instances. Indicators are per-run stateful (existing pattern with `_lastProcessedIndex`).
- [ ] **Optimization:** All tunable params use `[Optimizable]` attribute. Module params inherit `ModuleParamsBase`. `CartesianProductGenerator` and `OptimizationAxisResolver` discover module params via reflection (verify with AtrVolatilityFilterParams as reference).
- [ ] **Event bus:** Modules emit appropriate events (`SignalEvent`, `RiskEvent`, custom events) via `IEventBus`. All events implement `IBacktestEvent`. Exportable via existing JSONL sinks.
- [ ] **Debug probe:** Module state changes are observable through `IDebugProbe.OnBarProcessed()`. Stop level changes, regime transitions, and signal scores appear in debug snapshots.
- [ ] **Multi-subscription:** Modules that need multiple subscriptions (CrossAsset, MTF) receive bars from multiple subscriptions via the existing engine delivery loop. Modules align internally by timestamp.
- [ ] **Integer arithmetic:** All price-derived indicator outputs use `long` consistent with `Int64Bar`. Ratio indicators use scaled integers (e.g., RSI × 100 → range 0-10000).
- [ ] **Incremental computation:** Indicators use the existing pattern — `Compute()` called with growing `IReadOnlyList<Int64Bar>`, processing only from `_lastProcessedIndex + 1`. No recomputation of historical values.
