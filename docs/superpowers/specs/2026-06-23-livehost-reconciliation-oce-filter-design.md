# §D — Reconciliation-loop OCE filter fix (clean) — Design

**Date:** 2026-06-23
**Scope item:** §D of `docs/superpowers/specs/2026-06-23-livehost-data-plane-followups.md`
**Branch:** committed directly on `feat/livehost-data-plane` (Plan 4 unmerged; owner directive)
**Status:** Design approved; pending writing-plans → subagent-driven-development.

## Problem

`BinanceLiveConnector.RunReconciliationLoopAsync` (`src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceLiveConnector.cs:445–514`) runs the 3-phase order-group reconciliation on a `PeriodicTimer`. It has two stacked catch sites:

- **Inner** (per-session, `:500`): `catch (Exception ex) when (ex is not OperationCanceledException)` — intended to count transient failures and keep looping.
- **Outer** (`:513`): `catch (OperationCanceledException) { }` — intended to swallow shutdown and exit.

An `HttpClient.Timeout` inside `OrderGroupReconciler.DetectAsync` / `CancelOrphansAsync` throws `TaskCanceledException`, which **is-a** `OperationCanceledException`, **even though the caller token `ct` was never cancelled**. The inner filter *excludes* it, so it escapes the per-session handler, reaches the **outer** handler, and **breaks out of the `while` loop**. Reconciliation then silently stays dead for the connector's lifetime while `ct` is still live — on the money host. This is the failure mode recorded in `[[feedback_oce_filter_pattern]]` (HistoryLoader prod incident, 2026-04-21), here pre-existing from Plan 3 (not introduced by Plan 4).

## Decision

Adopt the repo's canonical shutdown-classification convention and, since this code is not in production, take the opportunity to make the loop structurally clean rather than minimal-diff. No backward-compatibility shims; nothing left dead.

### 1. Canonical `IsTrueShutdown` helper

Verbatim match to the four existing HistoryLoader sites (`ScheduledCollectorService`, `FundingInfoRefreshService`, `TickCanonicalizerService`), declared `internal static` so the test project (already wired via `InternalsVisibleTo "AlgoTradeForge.LiveHost.Infrastructure.Tests"`) can pin it directly:

```csharp
internal static bool IsTrueShutdown(Exception ex, CancellationToken stoppingToken) =>
    ex is OperationCanceledException oce
    && stoppingToken.IsCancellationRequested
    && oce.CancellationToken == stoppingToken;
```

### 2. Extract the per-session reconciliation body

The ~35-line three-phase block (the structure that hid the inverted filter) moves into a named method. Per the repo's no-`Async`-suffix convention (`[[feedback_no_async_suffix]]`), the new method takes **no** suffix, and since this change rewrites the loop method's body wholesale, its suffix is dropped too: `RunReconciliationLoopAsync` → `RunReconciliationLoop`. The pre-existing `DetectAsync`/`CancelOrphansAsync`/`WriteAsync` calls live on other classes not touched here, so they stay (incremental application).

```csharp
private async Task ReconcileSession(
    LiveSessionEntry entry, ITradeRegistryProvider provider, CancellationToken ct)
{
    // Phase 1: snapshot expected orders on the EventQueue (thread-safe read)
    // Phase 2: detect on the timer thread (exchange query + pure comparison)
    // Phase 3a: repair missing groups on the EventQueue
    // Phase 3b: cancel orphans directly on the exchange
}
```

The existing in-body comments documenting the `WriteAsync` (not `TryWrite`) rationale and the in-progress-fill edge case are preserved on the extracted method.

### 3. The loop reduces to its real control flow

```csharp
private async Task RunReconciliationLoop(CancellationToken ct)
{
    using var timer = new PeriodicTimer(_sharedOptions.ReconciliationInterval);
    var consecutiveFailures = 0;
    try
    {
        while (await timer.WaitForNextTickAsync(ct))
        {
            foreach (var entry in _sessions.Values)
            {
                if (entry.Strategy is not ITradeRegistryProvider provider)
                    continue;
                try
                {
                    await ReconcileSession(entry, provider, ct);
                    consecutiveFailures = 0;
                }
                catch (Exception ex) when (!IsTrueShutdown(ex, ct))
                {
                    consecutiveFailures++;
                    LogReconciliationFailure(ex, entry.SessionId, consecutiveFailures);
                }
            }
        }
    }
    catch (OperationCanceledException) { }
}
```

- Transient `HttpClient.Timeout` (OCE, `ct` live) → `!IsTrueShutdown` is `true` → caught, counted, **loop continues**.
- Genuine shutdown (`ct` cancelled, OCE carries `ct`) → `!IsTrueShutdown` is `false` → not caught here → propagates to the single outer `catch (OperationCanceledException) { }` → clean exit.
- Any non-OCE error → caught, counted (unchanged intent).

The outer `catch` remains the **one** shutdown exit; no second mechanism is introduced.

### 4. Fix the inverted failure logging (owner-approved cleanup)

Existing behavior logged `Error` for the first/second consecutive failure and *downgraded* to `Warning` at ≥3 — escalating failures got a quieter log. Corrected to the sensible direction in a small helper:

```csharp
private void LogReconciliationFailure(Exception ex, Guid sessionId, int consecutiveFailures)
{
    if (consecutiveFailures >= 3)
        _logger.LogError(ex,
            "Reconciliation has failed {Count} consecutive times for session {SessionId}",
            consecutiveFailures, sessionId);
    else
        _logger.LogWarning(ex,
            "Reconciliation failed for session {SessionId} (attempt {Count})",
            sessionId, consecutiveFailures);
}
```

A single transient blip is now a `Warning`; sustained failure (≥3) escalates to `Error`.

## Testing

Unit-test `IsTrueShutdown` directly — the predicate *is* the bug, and testing it is deterministic and fast (no timer, no live connector). New focused test file under `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/`:

1. **Regression guard** — a `TaskCanceledException` shaped like `HttpClient.Timeout` (`InnerException = TimeoutException`, `CancellationToken.None`) classified against a **live** (non-cancelled) token → expect `false` (must be treated as transient, not shutdown).
2. **Shutdown** — an `OperationCanceledException(token)` where `token` is a **cancelled** `CancellationToken` passed as `stoppingToken` → expect `true`.
3. **Non-OCE** — a plain `InvalidOperationException` against any token → expect `false`.

The full reconciliation-loop continuation is not integration-tested: the loop is private and constructs its `OrderGroupReconciler` internally, so an end-to-end test would be timing-dependent and high-setup for low marginal assurance. Pinning the predicate guards the exact regression.

## Scope & guardrails

- **One source file** (`BinanceLiveConnector.cs`) + **one test file**. No new packages, no public-surface signature changes.
- Money host, but the **order/execution path is untouched** — this only changes which exceptions the *reconciliation* loop tolerates vs. propagates. Behavior is strictly more resilient.
- Conventions: `internal static` helper matches repo convention; `using var timer` (already present) per resource-release convention; no `Async` suffix; one type per file unaffected (all changes within the existing class).
- No backward-compat shims; no dead code.

## Acceptance

- `IsTrueShutdown` exists, `internal static`, matching the HistoryLoader form.
- Inner filter is `when (!IsTrueShutdown(ex, ct))`; the per-session body is extracted to `ReconcileSession`; the loop method is renamed `RunReconciliationLoopAsync` → `RunReconciliationLoop` with its single caller at `:224` (`_reconcileTask = RunReconciliationLoop(_cts.Token);`) updated; the outer `catch (OperationCanceledException) { }` is the sole shutdown exit.
- Failure logging severity is corrected (≥3 → `Error`, else `Warning`).
- New `IsTrueShutdown` unit tests (3 cases) pass.
- `dotnet build AlgoTradeForge.slnx` clean; `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/` green (run sequentially, one dotnet process).

## Cross-references

- Pattern: `[[feedback_oce_filter_pattern]]` (the canonical `IsTrueShutdown` form + the 2026-04-21 incident).
- Parent scope: `docs/superpowers/specs/2026-06-23-livehost-data-plane-followups.md` §D.
- Next items (deferred this session): §A+§A′+§B unified subscription/capability effort, then Plans 5/6.
