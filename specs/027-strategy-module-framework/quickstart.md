# Quickstart: Strategy Module Framework

**Branch**: `027-strategy-module-framework` | **Date**: 2026-04-02

## What This Feature Does

Provides a modular base class (`ModularStrategyBase<TParams>`) that orchestrates a sealed three-phase bar-processing pipeline — Update Context, Manage Positions, Evaluate Entry — so that strategy developers only implement signal generation logic while getting position management, exit rules, trailing stops, filters, and sizing for free.

## Minimal Strategy Example

A strategy developer creates a new strategy by:
1. Inheriting from `ModularStrategyBase<TParams>` (instead of `StrategyBase<TParams>`)
2. Implementing the single abstract method `OnGenerateSignal()`
3. Optionally registering pre-built modules in `OnStrategyInit()`

```
Strategy class:
  - Inherits ModularStrategyBase<MyParams>
  - Overrides OnGenerateSignal() → returns signal strength [-100, +100] and direction
  - In OnStrategyInit(): creates indicators, registers filters

Params class:
  - Inherits ModularStrategyParamsBase
  - Adds strategy-specific [Optimizable] properties
  - Pipeline thresholds (FilterThreshold, SignalThreshold, ExitThreshold) come from base
```

## Pipeline Flow (Per Bar)

```
OnBarComplete(bar)
│
├─ Phase 1: UPDATE (runs for all subscriptions)
│  ├─ Context.Update(bar, equity, cash)
│  ├─ RegimeDetector?.Update(bar)          → writes regime to context
│  ├─ Strategy's OnContextUpdated(bar)     → optional hook
│  └─ No orders, no side effects
│
├─ Phase 2: MANAGE (primary subscription only, skipped when flat)
│  └─ For each active OrderGroup:
│     ├─ TrailingStop?.Update(groupId, bar) → ratchet stop
│     ├─ ExitModule?.Evaluate(bar, group)   → score [-100, +100]
│     ├─ Strategy's OnEvaluateExit()        → custom exit score
│     └─ If exit triggered → close group via TradeRegistry
│
└─ Phase 3: ENTER (primary subscription only)
   ├─ TradeRegistry.CanOpenNew()?           → capacity gate
   ├─ Filters.Evaluate(bar)                 → weighted score gate
   ├─ Strategy's OnGenerateSignal(bar)      → signal + direction [★ MUST IMPLEMENT]
   ├─ Strategy's OnGetEntryPrice(bar)       → price + order type [default: market]
   ├─ Strategy's OnGetRiskLevels(bar)       → SL + TPs [default: ATR-based]
   ├─ MoneyManagement.CalculateSize()       → quantity
   └─ Strategy's OnExecuteEntry()           → submit [default: single order]
```

## Available Modules

| Module | Purpose | Registration |
|--------|---------|-------------|
| TradeRegistryModule | Order group lifecycle (always present) | Automatic from params |
| MoneyManagementModule | Position sizing (always present) | Automatic from params |
| TrailingStopModule | Per-group trailing stop | `SetTrailingStop(new TrailingStopModule(params))` |
| ExitModule | Aggregates exit rules | `SetExit(exitModule)` after adding rules |
| RegimeDetectorModule | Market regime classification | `SetRegimeDetector(new RegimeDetectorModule(params))` |
| IFilterModule (various) | Entry filters with scored output | `AddFilter(filterInstance)` |

## Available Exit Rules

Compose exit behavior by adding rules to an ExitModule:
- TimeBasedExitRule — close after N bars
- ProfitTargetExitRule — close at N × ATR profit
- SignalReversalExitRule — close when signal flips
- RegimeChangeExitRule — close when regime changes
- SessionCloseExitRule — close at specific UTC hour
- CointegrationBreakExitRule — close when statistical relationship breaks (pairs)

## Key Differences from StrategyBase

| Aspect | StrategyBase | ModularStrategyBase |
|--------|-------------|-------------------|
| OnBarComplete | Virtual (you write everything) | Sealed (pipeline handles it) |
| Position management | Manual | Automatic via TradeRegistry + exit rules |
| Entry pipeline | Manual | Automatic with filter gate, sizing, validation |
| Signal generation | N/A | Abstract OnGenerateSignal() — you implement this |
| Trailing stop | Manual | Module with per-group state |
| Optimization | Direct [Optimizable] params | + nested module params auto-discovered |
| Live reconciliation | Manual ITradeRegistryProvider | Automatic (base implements it) |

## Build & Test

```bash
# Build
dotnet build AlgoTradeForge.slnx

# Test framework + model strategies
dotnet test tests/AlgoTradeForge.Domain.Tests/ --filter "Category=StrategyModules"

# Test with private strategies
dotnet build ../AlgoTradeForge.Private/AlgoTradeForge.Full.slnx
```
