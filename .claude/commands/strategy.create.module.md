---
description: Implement a pluggable strategy module (indicator wrapper, exit rule, trend filter, signal generator, etc.)
handoffs:
  - /agent.debug
---

## User Input

```text
$ARGUMENTS
```

## Instructions

You are implementing a pluggable strategy module for the AlgoTradeForge optimization and debug systems. Modules are composable strategy components — indicator wrappers, exit rules, entry filters, signal generators, sizing modules, etc. — that integrate with the optimization framework (discoverable via `[ModuleKey]` + `[Optimizable]`), the debug/event system (`IEventBus` / `IIndicatorFactory`), and the test pipeline.

### 1. Read Constitution

Read `.specify/memory/constitution.md` for code style rules. Key conventions:

- File-scoped namespaces
- Primary constructors where possible
- Int64 money convention (`long` for all Domain prices/monetary values)
- No XML doc comments (use `//` only where logic isn't self-evident)
- xUnit + NSubstitute for tests
- Serilog for logging

### 2. Parse & Classify the Module

From the user input, extract:

| Field | Description |
|---|---|
| **Name** | Module name (e.g. `AtrTrailingStop`, `TrendFilter`, `RsiExit`) |
| **Category** | One of: `Exit`, `Entry`, `Filter`, `Signal`, `Sizing`, `Risk` |
| **Behavior** | What the module does — activation conditions, output, reset logic |
| **Underlying indicator** | If the module wraps an indicator, which one (e.g. ATR, RSI, EMA) |
| **Direction handling** | Does it apply to longs, shorts, or both? |

**Module archetype classification** — determines the implementation pattern:

| Archetype | When | Event strategy | Example |
|---|---|---|---|
| **Indicator wrapper** | Module wraps a Domain indicator | Events auto-emitted by `EmittingIndicatorDecorator` — no manual emission needed | ATR trailing stop, EMA crossover filter |
| **Decision module** | Module makes buy/sell/exit decisions without wrapping an indicator | Emits `SignalEvent` via `IEventBus` with source `"module.{Name}"` | Time-based exit, position sizing |
| **Composite** | Both wraps an indicator AND makes decisions | Both `Initialize()` for indicator + `SetEventBus()` for signals | RSI entry with signal confirmation |

### 3. Clarify Gaps

If any of the following are unclear from the user input, **ask the user before proceeding**:

- What triggers the module? (bar complete, bar start, on-trade, on-fill?)
- What are the tunable parameters and their reasonable ranges?
- Does the module need to track state across bars (e.g. highest price since entry)?
- For exit modules: does it generate a signal or directly submit orders?
- For indicator wrappers: does the underlying indicator already exist in `src/AlgoTradeForge.Domain/Indicators/`?
- Direction handling: long-only, short-only, or both?
- Reset behavior: when does internal state reset? (new position, flat, etc.)

### 4. Check Prerequisites

**Search for existing slot interface:**

```
src/AlgoTradeForge.Domain/Strategy/Modules/{Category}/I{Category}Module.cs
```

If the slot interface for this category doesn't exist, you'll create it in step 6.

**If this is an indicator wrapper**, search for the target indicator:

```
src/AlgoTradeForge.Domain/Indicators/
```

If the indicator doesn't exist, implement it first following the `DeltaZigZag` pattern:
- Extend `Int64IndicatorBase`
- Constructor takes raw parameters (not a params class)
- Implement `Compute(IReadOnlyList<Int64Bar> series)`
- Add `IndicatorBuffer<long>` for each output
- Write tests in `tests/AlgoTradeForge.Domain.Tests/Indicators/`
- Build and verify tests pass before continuing

### 5. Plan File Layout

All files follow this structure:

```
src/AlgoTradeForge.Domain/Strategy/Modules/{Category}/
  I{Category}Module.cs          (slot interface, if new)
  {Name}Params.cs               (params class)
  {Name}Module.cs               (implementation)

tests/AlgoTradeForge.Domain.Tests/Strategy/Modules/{Category}/
  {Name}ModuleTests.cs          (unit tests)
```

### 6. Create Slot Interface (if new category)

Only if `I{Category}Module` doesn't already exist. Create it as:

```csharp
namespace AlgoTradeForge.Domain.Strategy.Modules.{Category};

public interface I{Category}Module : IStrategyModule
{
    // Minimal contract — only Domain types (Int64Bar, long, Asset)
    // Do NOT put IEventBus or IIndicatorFactory in the interface
}
```

Add the lifecycle methods appropriate for the category:

| Category | Typical contract methods |
|---|---|
| `Exit` | `bool ShouldExit(Int64Bar bar, long entryPrice, int direction)` |
| `Entry` | `int GetSignal(Int64Bar bar)` returns -1/0/+1 |
| `Filter` | `bool IsAllowed(Int64Bar bar, int direction)` |
| `Signal` | `SignalResult Evaluate(Int64Bar bar)` |
| `Sizing` | `decimal GetQuantity(long price, long cash, long risk)` |
| `Risk` | `bool CheckRisk(long exposure, long equity)` |

These are guidelines — adapt to the specific module's needs.

### 7. Create Params Class

```csharp
namespace AlgoTradeForge.Domain.Strategy.Modules.{Category};

public sealed class {Name}Params : ModuleParamsBase
{
    [Optimizable(Min = ..., Max = ..., Step = ...)]
    public {type} {ParamName} { get; init; } = {default};

    // Repeat for each tunable parameter
}
```

**Type rules for params:**
- `long` for prices and monetary values (Int64 money convention)
- `decimal` for ratios, percentages, multipliers
- `int` for periods, lookbacks, counts

**Every tunable numeric property** must have `[Optimizable(Min, Max, Step)]`.

### 8. Create Implementation

Apply the pattern based on the archetype from step 2:

#### Indicator Wrapper Pattern

```csharp
namespace AlgoTradeForge.Domain.Strategy.Modules.{Category};

[ModuleKey("{category}.{name}")]
public sealed class {Name}Module({Name}Params parameters) : I{Category}Module, IStrategyModule<{Name}Params>
{
    private {IndicatorType}? _indicator;

    public void Initialize(IIndicatorFactory factory, DataSubscription subscription)
    {
        _indicator = new {IndicatorType}(parameters.Param1, parameters.Param2);
        factory.Create(_indicator, subscription);
        // EmittingIndicatorDecorator handles event emission automatically
    }

    // Implement I{Category}Module contract methods using _indicator.Buffers
}
```

#### Decision Module Pattern

```csharp
namespace AlgoTradeForge.Domain.Strategy.Modules.{Category};

[ModuleKey("{category}.{name}")]
public sealed class {Name}Module({Name}Params parameters) : I{Category}Module, IStrategyModule<{Name}Params>, IEventBusReceiver
{
    private IEventBus _eventBus = NullEventBus.Instance;

    void IEventBusReceiver.SetEventBus(IEventBus bus) => _eventBus = bus;

    // Implement I{Category}Module contract methods
    // Emit signals when decisions are made:
    //   _eventBus.Emit(new SignalEvent(timestamp, "module.{Name}", ...));
}
```

#### Composite Pattern

Combines both `Initialize()` for indicator and `IEventBusReceiver` for signals.

**Key constraints for all patterns:**

- **Constructor takes only the params class** — required by `OptimizationStrategyFactory` which uses `Activator.CreateInstance(type, paramsInstance)`
- **`[ModuleKey("...")]`** on the class — required for optimization discovery
- **Implements `IStrategyModule<TParams>`** — required for generic param binding
- **Implements the slot interface** — required for strategy composition
- Other dependencies (`IIndicatorFactory`, `IEventBus`) via post-construction setters/methods, NOT constructor

### 9. Verify Optimization Compatibility

Before writing tests, verify this checklist:

- [ ] `[ModuleKey("category.name")]` attribute on class
- [ ] Single constructor taking only `{Name}Params`
- [ ] `{Name}Params` extends `ModuleParamsBase`
- [ ] `[Optimizable(Min, Max, Step)]` on every tunable numeric property
- [ ] Implements `IStrategyModule<{Name}Params>`
- [ ] Implements the slot interface (`I{Category}Module`)
- [ ] No `IEventBus` or `IIndicatorFactory` in constructor
- [ ] For decision modules: implements `IEventBusReceiver`, defaults to `NullEventBus.Instance`

### 10. Write Unit Tests

Create `tests/AlgoTradeForge.Domain.Tests/Strategy/Modules/{Category}/{Name}ModuleTests.cs`.

**Test categories to cover:**

| Category | What to test |
|---|---|
| Construction | Module creates with valid params, params defaults are sane |
| Core behavior | Primary logic with known inputs → expected outputs |
| Edge cases | Zero values, extremes, boundary conditions, first-bar behavior |
| Parameter sensitivity | Different param values produce different results |
| Event emission | Decision modules: verify `SignalEvent` via `CapturingEventBus` |
| Indicator delegation | Wrapper modules: verify indicator is created and consulted |

**Test utilities available:**

```csharp
using AlgoTradeForge.Domain.Tests.TestUtilities;  // TestBars, TestAssets
// CapturingEventBus is internal to the test project — use it directly
```

**Test patterns:**

```csharp
namespace AlgoTradeForge.Domain.Tests.Strategy.Modules.{Category};

public sealed class {Name}ModuleTests
{
    private readonly {Name}Params _defaultParams = new()
    {
        Param1 = ...,
        Param2 = ...
    };

    [Fact]
    public void ShouldDoExpectedBehavior_WhenCondition()
    {
        var module = new {Name}Module(_defaultParams);
        // Arrange, Act, Assert
    }

    [Fact]
    public void ShouldEmitSignal_WhenDecisionMade()
    {
        var bus = new CapturingEventBus();
        var module = new {Name}Module(_defaultParams);
        ((IEventBusReceiver)module).SetEventBus(bus);

        // Act — trigger decision
        // Assert — check bus.Events contains expected SignalEvent
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(100, false)]
    public void ShouldRespondToParameterChanges(int paramValue, bool expected)
    {
        var p = new {Name}Params { Param1 = paramValue };
        var module = new {Name}Module(p);
        // Assert behavior varies with params
    }
}
```

### 11. Build & Run Tests

Run in sequence — stop and fix if any step fails:

```bash
# 1. Build entire solution
dotnet build AlgoTradeForge.slnx

# 2. Run only the new module's tests
dotnet test tests/AlgoTradeForge.Domain.Tests --filter "FullyQualifiedName~{Name}Module"

# 3. Run full Domain test suite to catch regressions
dotnet test tests/AlgoTradeForge.Domain.Tests
```

If build fails, fix compilation errors. If tests fail, fix the tests or implementation. Do not proceed until green.

### 12. Integration Verification

Show the user how to integrate the module into a strategy. Example:

```csharp
// In strategy params class:
public sealed class MyStrategyParams : StrategyParamsBase
{
    [OptimizableModule]
    public {Name}Params {Name} { get; init; } = new();
}

// In strategy OnInit():
public override void OnInit()
{
    _{name}Module = new {Name}Module(Params.{Name});

    // For indicator wrappers:
    _{name}Module.Initialize(Indicators, Params.DataSubscriptions[0]);

    // For decision modules (event bus is set automatically by engine
    // via IEventBusReceiver, but for manual wiring):
    // ((IEventBusReceiver)_{name}Module).SetEventBus(EventBus);
}

// In OnBarComplete():
public override void OnBarComplete(Int64Bar bar, DataSubscription sub, IOrderContext orders)
{
    // Use the module
    if (_{name}Module.ShouldExit(bar, _entryPrice, _direction))
        orders.Submit(exitOrder);
}
```

### 13. Debug Acceptance Test

This is the final acceptance condition. Run a backtest using the `/backtest` skill with a strategy that hosts this module, then use `/agent.debug` to verify:

- For **indicator wrappers**: `ind` events with the indicator name appear in `events.jsonl`
- For **decision modules**: `sig` events with source `"module.{Name}"` appear in `events.jsonl`
- Events have correct timestamps and well-formed payloads

If no strategy currently hosts this module, show the user the integration code from step 12 and note that the debug acceptance test should be run after the module is integrated into a strategy.

### 14. Summary

Print a summary of what was created:

```
Module: {Name}Module ({Category})
Archetype: {Indicator Wrapper | Decision Module | Composite}

Files created:
  src/.../Modules/{Category}/I{Category}Module.cs     (if new)
  src/.../Modules/{Category}/{Name}Params.cs
  src/.../Modules/{Category}/{Name}Module.cs
  tests/.../{Category}/{Name}ModuleTests.cs
  src/.../Indicators/{IndicatorName}.cs                (if new indicator needed)
  tests/.../Indicators/{IndicatorName}Tests.cs         (if new indicator needed)

Optimization axes:
  {ParamName}: {Min} to {Max}, step {Step} ({count} values)
  ...
  Total combinations: {product}

Tests: {count} passed

Integration: Add [{Name}Params] with [OptimizableModule] to your strategy params,
initialize in OnInit(), use in OnBarComplete().
```

---

## Reference: Type-to-File Mapping

| Type | File |
|---|---|
| `IStrategyModule`, `IStrategyModule<T>` | `src/AlgoTradeForge.Domain/Strategy/Modules/IStrategyModule.cs` |
| `ModuleParamsBase` | `src/AlgoTradeForge.Domain/Strategy/Modules/ModuleParamsBase.cs` |
| `[ModuleKey]` | `src/AlgoTradeForge.Domain/Optimization/Attributes/ModuleKeyAttribute.cs` |
| `[Optimizable]` | `src/AlgoTradeForge.Domain/Optimization/Attributes/OptimizableAttribute.cs` |
| `[OptimizableModule]` | `src/AlgoTradeForge.Domain/Optimization/Attributes/OptimizableModuleAttribute.cs` |
| `[StrategyKey]` | `src/AlgoTradeForge.Domain/Optimization/Attributes/StrategyKeyAttribute.cs` |
| `IEventBus` | `src/AlgoTradeForge.Domain/Events/IEventBus.cs` |
| `IEventBusReceiver` | `src/AlgoTradeForge.Domain/Events/IEventBusReceiver.cs` |
| `NullEventBus` | `src/AlgoTradeForge.Domain/Events/NullEventBus.cs` |
| `SignalEvent` | `src/AlgoTradeForge.Domain/Events/SignalEvents.cs` |
| `IIndicatorFactory` | `src/AlgoTradeForge.Domain/Indicators/IIndicatorFactory.cs` |
| `Int64IndicatorBase` | `src/AlgoTradeForge.Domain/Indicators/Int64IndicatorBase.cs` |
| `DeltaZigZag` (reference impl) | `src/AlgoTradeForge.Domain/Indicators/DeltaZigZag.cs` |
| `EmittingIndicatorDecorator` | `src/AlgoTradeForge.Application/Indicators/EmittingIndicatorDecorator.cs` |
| `StrategyBase<T>` | `src/AlgoTradeForge.Domain/Strategy/StrategyBase.cs` |
| `StrategyParamsBase` | `src/AlgoTradeForge.Domain/Strategy/StrategyParamsBase.cs` |
| `SpaceDescriptorBuilder` | `src/AlgoTradeForge.Infrastructure/Optimization/SpaceDescriptorBuilder.cs` |
| `OptimizationStrategyFactory` | `src/AlgoTradeForge.Infrastructure/Optimization/OptimizationStrategyFactory.cs` |
| `TestBars` | `tests/AlgoTradeForge.Domain.Tests/TestUtilities/TestBars.cs` |
| `TestAssets` | `tests/AlgoTradeForge.Domain.Tests/TestUtilities/TestAssets.cs` |
| `CapturingEventBus` | `tests/AlgoTradeForge.Domain.Tests/TestUtilities/CapturingEventBus.cs` |
| Constitution | `.specify/memory/constitution.md` |
