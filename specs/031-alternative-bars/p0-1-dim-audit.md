# P0-1 — DIM audit on `IFeedContext` (Phase 0)

**Status:** PASS — Phase 2b can ship default-interface-method (DIM) extensions to `IFeedContext` directly. P0-2 (`ISidecarReceiver` fallback) is **skipped**.

## What was audited

The TRD §11 Phase 0 mandate: confirm that adding default-interface-methods to `IFeedContext` (specifically `TryGetPrimarySidecar` and `PrimarySidecarSchema` in Phase 2b) will dispatch correctly when a strategy class living in a plugin assembly is JIT'd against the new interface metadata.

## Findings

### Implementations enumerated

Two `IFeedContext` impls exist across **public + private repos**:

| Impl | Path | Assembly |
|---|---|---|
| `BacktestFeedContext` | `src/AlgoTradeForge.Domain/Engine/BacktestFeedContext.cs:10` | `AlgoTradeForge.Domain` (net10.0) |
| `NullFeedContext` | `src/AlgoTradeForge.Domain/Strategy/NullFeedContext.cs:9` | `AlgoTradeForge.Domain` (net10.0) |

Zero implementations in `../AlgoTradeForge.Private/` (no plugin owns its own `IFeedContext`).

### Plugin loading model

`PluginLoader` (`src/AlgoTradeForge.Infrastructure/Plugins/PluginLoader.cs:29-44`) loads plugin assemblies into `AssemblyLoadContext.Default` with an `AssemblyDependencyResolver` chained to the default-context resolution event. **No isolated ALC** — plugins share the runtime's default load context with the host.

### Target frameworks

Public-repo projects target `net10.0`. `../AlgoTradeForge.Private/src/AlgoTradeForge.Strategies.Private/AlgoTradeForge.Strategies.Private.csproj:4` also `net10.0`. Single TFM across the boundary; no cross-target shimming needed.

### DIM dispatch reasoning

When a strategy class in a plugin assembly references `IFeedContext` and the runtime sees the host-supplied interface metadata (which now carries DIMs), virtual-method resolution in CLR follows the standard rule: a class implementing the interface dispatches to its own override if present, else to the interface's DIM. Because the plugin shares `AssemblyLoadContext.Default` with the host, there is **one** `IFeedContext` `RuntimeType` in the AppDomain — the plugin and host both bind to the same interface metadata, so the DIM is visible.

No blockers found:
- No `ReflectionOnly` loading.
- No isolated/collectible ALCs around plugins.
- No `[InternalsVisibleTo]` constraints that would gate DIM visibility.
- C# 14 supports DIMs natively; net10.0 runtime supports DIM dispatch unconditionally.

## Decision

Phase 2b ships `IFeedContext.TryGetPrimarySidecar(out ReadOnlySpan<double> values) { values = default; return false; }` and `PrimarySidecarSchema => null` as DIMs.

**P0-2 is skipped** — no `ISidecarReceiver` fallback shape needed.

## Re-audit triggers

This decision must be revisited if any of the following change:
- A future plugin loader adopts isolated `AssemblyLoadContext` (e.g., for hot-reload or version isolation).
- A plugin starts shipping its own `IFeedContext` impl whose target TFM differs from the host's.
- DIM dispatch semantics change in a future .NET release (none anticipated; the language rule is stable).
