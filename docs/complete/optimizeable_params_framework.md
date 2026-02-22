# Optimizable Parameters Framework — Design Analysis

## 1. The Search Space Model (Theory)

The optimization space has **three fundamentally different axis types**:

| Axis Type | Example | Cardinality |
|---|---|---|
| **Numeric Range** | `DzzDepth: [1..10, step 0.5]` | `(max-min)/step + 1` |
| **Discrete Set** | `DataSubscription: {BTCUSDT+H1, ETHUSDT+H1}` | `\|values\|` |
| **Module Choice** (tagged union) | `ExitStrategy: {AtrExit(mult,period) \| FibTp(level,n) \| RsiRev(threshold)}` | `Σ(variant sub-products)` |

The total cartesian product is:

```
Total = ∏(numeric axes) × ∏(discrete axes) × ∏(module axes)

where each module axis contributes:
  Σ over variants v: ∏(v's sub-axes)
```

For example: 19 DzzDepth values × 2 subscriptions × (4×5 AtrExit + 3×3 FibTp + 5 RsiRev) = 19 × 2 × 34 = **1,292 combinations**.

Module variants can themselves contain `[OptimizableModule]` slots, forming a **recursive tree** of arbitrary depth. The cartesian product generator traverses this tree naturally — the algorithm is the same regardless of depth. A configurable **max-depth guard** (default 10) prevents accidental infinite recursion from circular references, but there is no hard architectural nesting constraint. Combinatorial explosion from deep nesting is the natural practical limiter.

## 2. Type System Design

### 2a. Optimization Space Descriptor (the "shape" declared in code)

```
OptimizableSpace (per strategy type)
├── ParameterAxis[]           — numeric/discrete axes from StrategyParamsBase
└── ModuleSlot[]              — pluggable module slots
    └── ModuleVariant[]       — registered implementations per slot
        ├── ParameterAxis[]   — numeric/discrete axes from ModuleParamsBase
        └── ModuleSlot[]      — nested module slots (recursive, same structure)
```

Each `ParameterAxis` is one of:
- `NumericRangeAxis(name, min, max, step, clrType)` — for `decimal`, `long`, `int`
- `DiscreteSetAxis(name, values[])` — for `DataSubscription`, enums, etc.

### 2b. How it maps to existing types

**Current state:**
- `StrategyBase<TParams>` where `TParams : StrategyParamsBase` — the strategy
- `ZigZagBreakoutParams.DzzDepth` — a fixed `decimal` value
- `DataSubscription(Asset, TimeFrame)` — a fixed subscription
- No module abstraction exists yet

**Proposed additions to the type hierarchy:**

```
StrategyParamsBase                    (existing — add module slot properties)
├── DataSubscriptions                 (existing — becomes a discrete-set axis)
├── numeric properties                (existing — annotated with [Optimizable])
└── IStrategyModule properties        (NEW — module slot references)

IStrategyModule                       (NEW — marker interface for pluggable modules)
├── IStrategyModule<TParams>          (NEW — generic, TParams : ModuleParamsBase)

ModuleParamsBase                      (NEW — like StrategyParamsBase but for modules)
├── numeric/primitive properties      (annotated with [Optimizable])
└── IStrategyModule properties        (recursive — nested module slots allowed)
```

### 2c. Concrete example of how ZigZagBreakout would evolve

```csharp
// The strategy params declare what's optimizable
public class ZigZagBreakoutParams : StrategyParamsBase
{
    [Optimizable(Min = 1, Max = 20, Step = 0.5)]
    public decimal DzzDepth { get; init; } = 5m;

    [Optimizable(Min = 10, Max = 100, Step = 10)]
    public long MinimumThreshold { get; init; } = 10L;

    [Optimizable(Min = 0.5, Max = 3, Step = 0.5)]
    public decimal RiskPercentPerTrade { get; init; } = 1m;

    // Module slot — the exit strategy is pluggable
    [OptimizableModule]
    public IExitModule? ExitModule { get; init; }
}
```

Module implementations:

```csharp
[ModuleKey("AtrExit")]
public class AtrExitModule(AtrExitParams p) : IExitModule { ... }

public class AtrExitParams : ModuleParamsBase
{
    [Optimizable(Min = 1.5, Max = 4.0, Step = 0.5)]
    public decimal Multiplier { get; init; } = 2.0m;

    [Optimizable(Min = 10, Max = 30, Step = 5)]
    public int Period { get; init; } = 14;
}
```

## 3. JSON Web Request Contract

The JSON mirrors the code-declared space but lets the caller **select and constrain** what to optimize in a given run:

```json
{
  "strategyName": "ZigZagBreakout",
  "optimizationAxes": {
    "DzzDepth": { "min": 2, "max": 8, "step": 1 },
    "RiskPercentPerTrade": { "fixed": 1.0 },
    "DataSubscriptions": [
      { "asset": "BTCUSDT", "exchange": "Binance", "timeFrame": "01:00:00" },
      { "asset": "ETHUSDT", "exchange": "Binance", "timeFrame": "01:00:00" }
    ],
    "ExitModule": {
      "variants": {
        "AtrExit": {
          "Multiplier": { "min": 1.5, "max": 3.0, "step": 0.5 },
          "Period": { "min": 14, "max": 28, "step": 7 }
        },
        "FibonacciTp": {
          "Level": { "min": 0.382, "max": 0.786, "step": 0.202 },
          "NumLevels": { "fixed": 2 }
        }
      }
    }
  },
  "backtestOptions": {
    "initialCash": 10000,
    "startTime": "2024-01-01T00:00:00Z",
    "endTime": "2025-01-01T00:00:00Z",
    "commissionPerTrade": 0.1,
    "slippageTicks": 1
  }
}
```

**Validation rules** (JSON vs code descriptors):
- Parameter names must exist in the code-declared space
- Numeric ranges must be within code-declared bounds
- Module variant keys must match registered `[ModuleKey]` types
- Omitted axes use the code-declared defaults (fixed value, not range)

## 4. Architecture — Key Components

```
┌─────────────────────────────────────────────────────────┐
│  JSON Request                                           │
│  (web API: POST /api/optimization/run)                  │
└────────────────────┬────────────────────────────────────┘
                     │ deserialize
                     ▼
┌─────────────────────────────────────────────────────────┐
│  OptimizationRequest                                    │
│  (DTO — strategy name + axes + backtest options)        │
└────────────────────┬────────────────────────────────────┘
                     │ validate against
                     ▼
┌─────────────────────────────────────────────────────────┐
│  IOptimizationSpaceDescriptor                           │
│  (code-declared — built from reflection + attributes)   │
│  One per strategy type, cached at startup               │
└────────────────────┬────────────────────────────────────┘
                     │ merge & expand
                     ▼
┌─────────────────────────────────────────────────────────┐
│  CartesianProductGenerator                              │
│  Enumerates all ParameterCombination instances           │
│  (lazy IEnumerable to avoid materializing millions)     │
└────────────────────┬────────────────────────────────────┘
                     │ for each combination
                     ▼
┌─────────────────────────────────────────────────────────┐
│  IStrategyFactory.Create(combination)                   │
│  - Instantiate TParams from numeric values              │
│  - Resolve module slots via ModuleRegistry              │
│  - Set DataSubscriptions from combination               │
│  → Returns IInt64BarStrategy                            │
└────────────────────┬────────────────────────────────────┘
                     │
                     ▼
┌─────────────────────────────────────────────────────────┐
│  BacktestEngine.Run(...)  (existing — no changes)       │
│  → BacktestResult + PerformanceMetrics                  │
└────────────────────┬────────────────────────────────────┘
                     │ collect
                     ▼
┌─────────────────────────────────────────────────────────┐
│  OptimizationResult                                     │
│  - All combinations + their metrics                     │
│  - Sorted by objective (e.g., Sharpe, NetProfit)        │
│  - Best N results highlighted                           │
└─────────────────────────────────────────────────────────┘
```

## 5. Key Interfaces & Types (Proposed)

```csharp
// ── Axis descriptors (the "shape" of the search space) ──

public abstract record ParameterAxis(string Name);

public sealed record NumericRangeAxis(
    string Name, decimal Min, decimal Max, decimal Step,
    Type ClrType /* decimal, long, int */) : ParameterAxis(Name);

public sealed record DiscreteSetAxis(
    string Name, IReadOnlyList<object> Values,
    Type ClrType) : ParameterAxis(Name);

public sealed record ModuleSlotAxis(
    string Name, Type ModuleInterface,
    IReadOnlyList<ModuleVariantDescriptor> Variants) : ParameterAxis(Name);

public sealed record ModuleVariantDescriptor(
    string TypeKey, Type ImplType, Type ParamsType,
    IReadOnlyList<ParameterAxis> Axes);  // may include nested ModuleSlotAxis (recursive)

// ── Space descriptor (one per strategy) ──

public interface IOptimizationSpaceDescriptor
{
    string StrategyName { get; }
    Type StrategyType { get; }
    Type ParamsType { get; }
    IReadOnlyList<ParameterAxis> Axes { get; }  // includes module slots
}

// ── A single point in the search space ──

public sealed class ParameterCombination
{
    public IReadOnlyDictionary<string, object> Values { get; }
    // includes: "DzzDepth" → 5.0m, "DataSubscription" → DataSubscription(...),
    //           "ExitModule" → ("AtrExit", { "Multiplier": 2.0m, "Period": 14 })
}

// ── Module registry ──

public interface IModuleRegistry
{
    IReadOnlyList<ModuleVariantDescriptor> GetVariants(Type moduleInterface);
    IStrategyModule CreateModule(string typeKey, IDictionary<string, object> parameters);
}

// ── Cartesian product ──

public interface ICartesianProductGenerator
{
    IEnumerable<ParameterCombination> Enumerate(
        IOptimizationSpaceDescriptor space,
        OptimizationRequest request);
    long EstimateCount(/* same args */);  // for UI progress
}

// ── Optimization runner ──

public interface IOptimizationRunner
{
    Task<OptimizationResult> RunAsync(
        OptimizationRequest request, CancellationToken ct);
}
```

## 6. Attribute-Based Descriptor Discovery

The code-side declarations use attributes, and a reflection-based builder creates descriptors at startup:

```csharp
// On numeric properties
[AttributeUsage(AttributeTargets.Property)]
public sealed class OptimizableAttribute : Attribute
{
    public double Min { get; init; }
    public double Max { get; init; }
    public double Step { get; init; }
}

// On module slot properties
[AttributeUsage(AttributeTargets.Property)]
public sealed class OptimizableModuleAttribute : Attribute { }

// On module implementation classes
[AttributeUsage(AttributeTargets.Class)]
public sealed class ModuleKeyAttribute(string key) : Attribute
{
    public string Key => key;
}

// On strategy classes (maps name → type for JSON)
[AttributeUsage(AttributeTargets.Class)]
public sealed class StrategyKeyAttribute(string key) : Attribute
{
    public string Key => key;
}
```

A `SpaceDescriptorBuilder` scans assemblies at startup:
1. Find all `StrategyBase<TParams>` with `[StrategyKey]`
2. Reflect on `TParams` properties for `[Optimizable]` and `[OptimizableModule]`
3. For module slots, find all implementations of the interface with `[ModuleKey]`
4. Reflect on module params recursively (same rules: `[Optimizable]` + `[OptimizableModule]`)
5. Detect circular references via a visited-set; abort with error if a cycle is found
6. Enforce configurable max depth (default 10) during recursive scan
7. Cache descriptors in a dictionary keyed by strategy name

### Attributes vs Web Request — Dual-Role Design

Attributes and web request JSON play **complementary roles**:

| Concern | `[Optimizable]` Attribute | Web Request JSON |
|---|---|---|
| **Role** | Declares what CAN be optimized (eligibility + safety bounds) | Declares what TO optimize in this run |
| **Required?** | Yes — a property without `[Optimizable]` is invisible to the framework | Optional — overrides are per-run |
| **Ranges** | Outer envelope (max allowable bounds) | Sub-range within attribute bounds |

**Resolution flow per parameter:**

```
Attribute [Optimizable(Min=1, Max=20, Step=0.5)]    ← code declares the envelope
    │
    ▼
Web request { "min": 3, "max": 10, "step": 1 }      ← caller narrows for this run
    │
    ▼
Validation: 3 >= 1 ✓, 10 <= 20 ✓, 1 >= 0.5 ✓        ← bounds check
    │
    ▼
Generator iterates [3, 4, 5, 6, 7, 8, 9, 10]         ← effective range
```

**Three scenarios per parameter in a web request:**

1. **Range provided** (`{ "min": 3, "max": 10, "step": 1 }`) — validated against attribute bounds, iterated in this run
2. **Fixed value** (`{ "fixed": 5 }`) — parameter locked at that value, not iterated
3. **Omitted entirely** — uses the property's default value (from `init`), not iterated

This means attributes are the **opt-in gate**: only properties marked `[Optimizable]` are exposed to the optimization framework. The web request can then freely narrow, fix, or skip any eligible parameter — but never widen beyond the code-declared bounds.

## 7. Indicators as Pluggable Modules

Indicators are a natural fit for the module system. A strategy may want to optimize WHICH indicator to use (e.g., EMA vs SMA as a trend filter) alongside its numeric params.

### 7.1 `[OptimizableModule]` is Interface-Agnostic

The `[OptimizableModule]` attribute is not restricted to `IStrategyModule`-typed properties. It works on **any interface-typed property**, including `IIndicator<TInp, TBuff>`. The optimization framework only cares that:
- The property is interface-typed (so multiple implementations can be registered)
- Implementations are annotated with `[ModuleKey]`
- Implementation params classes have `[Optimizable]` properties

This means indicators don't need to implement `IStrategyModule` — they remain pure `IIndicator<TInp, TBuff>` implementations. The framework treats them the same as any other pluggable slot.

### 7.2 Example: Pluggable Trend Filter

```csharp
public class MomentumParams : StrategyParamsBase
{
    [Optimizable(Min = 0.5, Max = 3, Step = 0.5)]
    public decimal RiskPercentPerTrade { get; init; } = 1m;

    // Pluggable indicator — which trend filter to use
    [OptimizableModule]
    public IIndicator<Int64Bar, long>? TrendFilter { get; init; }
}
```

With registered variants:

```csharp
[ModuleKey("Sma")]
public class SimpleMovingAverage(SmaParams p) : Int64IndicatorBase { ... }

public class SmaParams : ModuleParamsBase
{
    [Optimizable(Min = 10, Max = 50, Step = 5)]
    public int Period { get; init; } = 20;
}

[ModuleKey("Ema")]
public class ExponentialMovingAverage(EmaParams p) : Int64IndicatorBase { ... }

public class EmaParams : ModuleParamsBase
{
    [Optimizable(Min = 10, Max = 50, Step = 5)]
    public int Period { get; init; } = 20;

    [Optimizable(Min = 0.1, Max = 0.5, Step = 0.1)]
    public decimal Smoothing { get; init; } = 0.2m;
}
```

JSON request for this slot:

```json
"TrendFilter": {
  "variants": {
    "Sma": { "Period": { "min": 10, "max": 30, "step": 10 } },
    "Ema": { "Period": { "fixed": 20 }, "Smoothing": { "min": 0.1, "max": 0.3, "step": 0.1 } }
  }
}
```

### 7.3 Composition with `IIndicatorFactory` (Debug/Event System)

The debug feature (see `debug-feature-requirements-v2.md` §10) introduces `IIndicatorFactory` — a **decoration factory** that wraps indicators with an `EmittingIndicator<TInp, TBuff>` decorator for event logging. This is a separate concern from the optimization module system, and the two compose cleanly:

```
Optimization picks WHICH indicator to create    (variant selection + params)
    │
    ▼
ModuleRegistry creates raw indicator instance   (e.g., new Sma(SmaParams{Period=20}))
    │
    ▼  strategy receives it via params, then wraps it:
IIndicatorFactory.Create(rawIndicator, sub)     (debug decoration layer)
    │
    ▼
EmittingIndicator<Int64Bar, long> or passthrough (depending on execution mode)
```

**In strategy code, the pattern is uniform** regardless of whether the indicator was manually constructed or injected via optimization:

```csharp
public override void OnInit()
{
    // Case 1: hardcoded indicator (params come from strategy params)
    _dzz = indicatorFactory.Create(
        new DeltaZigZag(Params.DzzDepth / 10m, Params.MinimumThreshold),
        DataSubscriptions[0]);

    // Case 2: pluggable indicator (injected by optimization module system)
    _trendFilter = indicatorFactory.Create(
        Params.TrendFilter!,
        DataSubscriptions[0]);
}
```

Both paths converge at `IIndicatorFactory.Create()`, which handles event emission wrapping. The strategy doesn't know or care whether the indicator was constructed manually or by the module registry.

### 7.4 Factory Selection by Execution Mode

| Mode | `IIndicatorFactory` impl | Behavior |
|---|---|---|
| Debug / Backtest | `EmittingIndicatorFactory` | Wraps with `EmittingIndicator` → emits `ind` events |
| Optimization | `PassthroughIndicatorFactory` | Returns raw indicator → zero overhead |

This is a setup-time decision by the optimization runner. Each parallel backtest trial gets the passthrough factory, since `ind` events are filtered out in `Optimization` export mode anyway (see debug doc §2.3).

## 8. Key Design Decisions

| Decision | Option A | Option B | Recommendation |
|---|---|---|---|
| **Descriptor source** | Attributes on params | Explicit `GetOptimizationSpace()` static method | **Attributes** — less boilerplate, enforces co-location with params |
| **Module params base** | Separate `ModuleParamsBase` | Reuse `StrategyParamsBase` | **Separate** — clearer intent, even though both support nested modules |
| **Module nesting** | Hard depth constraint | Configurable max-depth guard (default 10) | **Configurable guard** — no architectural limit, combinatorial explosion is the natural limiter |
| **DataSubscription axis** | Special-cased in the framework | Just a `DiscreteSetAxis` like any other | **DiscreteSet** — uniform treatment, simpler generator |
| **Cartesian product** | Eager `List<Combination>` | Lazy `IEnumerable` with `EstimateCount` | **Lazy** — handles large spaces without OOM |
| **Parallelism** | Sequential backtest loop | `Parallel.ForEachAsync` with degree of parallelism | **Parallel** — backtests are CPU-bound, embarrassingly parallel |
| **Result storage** | In-memory only | Stream to disk/DB as results come in | Start **in-memory**, add streaming later |
| **Indicator as module** | Fold into strategy params | First-class `[OptimizableModule]` slots | **Module slots** — `[OptimizableModule]` on `IIndicator<,>` properties; composes with `IIndicatorFactory` decoration from debug feature |

## 9. Proposed File Layout

```
src/AlgoTradeForge.Domain/
├── Optimization/                          (NEW)
│   ├── Attributes/
│   │   ├── OptimizableAttribute.cs
│   │   ├── OptimizableModuleAttribute.cs
│   │   ├── ModuleKeyAttribute.cs
│   │   └── StrategyKeyAttribute.cs
│   ├── Space/
│   │   ├── ParameterAxis.cs              (hierarchy)
│   │   ├── ModuleVariantDescriptor.cs
│   │   ├── IOptimizationSpaceDescriptor.cs
│   │   └── ParameterCombination.cs
│   ├── ICartesianProductGenerator.cs
│   ├── IOptimizationRunner.cs
│   └── OptimizationResult.cs
├── Strategy/
│   ├── Modules/                           (NEW)
│   │   ├── IStrategyModule.cs
│   │   ├── ModuleParamsBase.cs
│   │   ├── Exit/
│   │   │   ├── IExitModule.cs
│   │   │   ├── AtrExitModule.cs
│   │   │   └── FibonacciTpModule.cs
│   │   └── ...
│   └── (existing files)

src/AlgoTradeForge.Application/
├── Optimization/                          (NEW)
│   ├── RunOptimizationCommand.cs
│   ├── RunOptimizationCommandHandler.cs
│   └── OptimizationRequest.cs            (JSON DTO)

src/AlgoTradeForge.Infrastructure/         (or Domain)
├── Optimization/                          (NEW)
│   ├── SpaceDescriptorBuilder.cs         (reflection-based)
│   ├── CartesianProductGenerator.cs
│   ├── OptimizationRunner.cs
│   └── ModuleRegistry.cs
```

The backtest engine itself needs **zero changes** — it runs one strategy at a time as it does today. The optimization runner wraps it in a parallel loop.
