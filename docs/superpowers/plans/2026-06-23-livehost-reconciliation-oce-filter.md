# §D — Reconciliation-loop OCE filter fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the `BinanceLiveConnector` reconciliation timer loop from silently dying on a transient `HttpClient.Timeout`, and leave the loop structurally clean.

**Architecture:** Add the repo-canonical `internal static IsTrueShutdown(ex, ct)` predicate, rewrite the loop's inner per-session catch filter to `when (!IsTrueShutdown(ex, ct))`, extract the three-phase reconciliation body into `ReconcileSession`, drop the `Async` suffix on the rewritten loop method, and correct the inverted failure-log severity. Tests pin `IsTrueShutdown` directly — the predicate is the bug.

**Tech Stack:** C# 14 / .NET 10, xUnit, `Microsoft.Extensions.Logging`. No new packages.

**Spec:** `docs/superpowers/specs/2026-06-23-livehost-reconciliation-oce-filter-design.md`

## Global Constraints

- **One `dotnet` process at a time.** Build/test strictly sequential. Use `powershell.exe`, never `pwsh`.
- **No `Async` suffix** on new/rewritten async methods (signature conveys async). Pre-existing `DetectAsync`/`CancelOrphansAsync`/`WriteAsync` on *other* classes are untouched and keep their names.
- **No `catch when (ex is not OperationCanceledException)`** in long-running loops. Use `IsTrueShutdown(ex, ct)`.
- **One type per file**; all changes here are within the existing `BinanceLiveConnector` class + one new test file.
- **No backward-compat shims; no dead code left behind.**
- Commit messages: bash heredoc + `git commit -F -` (never PowerShell `Out-File` — UTF-8 BOM). End with the `Co-Authored-By` + `Claude-Session` trailers.
- **Implementer must NOT commit** — the controller stages + commits after verifying the diff. (Hook denies subagent `git add`.) Each task's "Commit" step is performed by the controller.

---

### Task 1: `IsTrueShutdown` predicate + unit tests

**Files:**
- Modify: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceLiveConnector.cs` (add one `internal static` method)
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/ReconciliationShutdownClassificationTests.cs` (new)

**Interfaces:**
- Produces: `internal static bool BinanceLiveConnector.IsTrueShutdown(Exception ex, CancellationToken stoppingToken)` — `true` only for genuine cooperative shutdown (an `OperationCanceledException` whose token equals `stoppingToken` AND `stoppingToken.IsCancellationRequested`); `false` for transient `HttpClient.Timeout` (`TaskCanceledException` with `ct` live) and all non-OCE exceptions.
- Consumes: nothing. `InternalsVisibleTo "AlgoTradeForge.LiveHost.Infrastructure.Tests"` already exists in the Infrastructure `.csproj`.

- [ ] **Step 1: Write the failing tests**

Create `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/ReconciliationShutdownClassificationTests.cs`. Use the same file-scoped namespace as the sibling tests in that folder (`namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;`).

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public class ReconciliationShutdownClassificationTests
{
    [Fact]
    public void HttpTimeout_WithLiveToken_IsNotShutdown()
    {
        // Shape of what HttpClient.Timeout throws: TaskCanceledException(inner: TimeoutException),
        // no caller-token cancellation.
        var timeout = new TaskCanceledException("The request timed out.", new TimeoutException());
        using var cts = new CancellationTokenSource(); // live, never cancelled

        Assert.False(BinanceLiveConnector.IsTrueShutdown(timeout, cts.Token));
    }

    [Fact]
    public void Oce_CarryingCancelledStoppingToken_IsShutdown()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var oce = new OperationCanceledException(cts.Token);

        Assert.True(BinanceLiveConnector.IsTrueShutdown(oce, cts.Token));
    }

    [Fact]
    public void NonOce_IsNotShutdown()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // even with a cancelled token, a non-OCE is a real failure

        Assert.False(BinanceLiveConnector.IsTrueShutdown(new InvalidOperationException("boom"), cts.Token));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `powershell.exe -NoProfile -Command "dotnet build AlgoTradeForge.slnx"`
Expected: **compile error** — `BinanceLiveConnector` does not contain a definition for `IsTrueShutdown`.

- [ ] **Step 3: Add the predicate**

In `BinanceLiveConnector.cs`, add this method to the class (place it just above `RunReconciliationLoopAsync`, near `:445`). Match the four existing HistoryLoader sites verbatim:

```csharp
internal static bool IsTrueShutdown(Exception ex, CancellationToken stoppingToken) =>
    ex is OperationCanceledException oce
    && stoppingToken.IsCancellationRequested
    && oce.CancellationToken == stoppingToken;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `powershell.exe -NoProfile -Command "dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter ReconciliationShutdownClassificationTests"`
Expected: **PASS**, 3 tests.

- [ ] **Step 5: Commit** (controller performs this)

```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceLiveConnector.cs \
        tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/ReconciliationShutdownClassificationTests.cs
git commit -F - <<'EOF'
fix(livehost): add IsTrueShutdown predicate for reconciliation loop

§D step 1. Canonical internal-static shutdown classifier matching the
HistoryLoader sites; unit tests pin the regression: an HttpClient.Timeout
(TaskCanceledException, ct live) must classify as NOT shutdown.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_018w22NAfM8bQwp5TTiMMGbX
EOF
```

---

### Task 2: Rewrite the reconciliation loop (filter, extraction, rename, logging)

**Files:**
- Modify: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceLiveConnector.cs`
  - `RunReconciliationLoopAsync` body (`:445–514`)
  - the single caller (`:224`)

**Interfaces:**
- Consumes: `IsTrueShutdown` (Task 1); existing `LiveSessionEntry` (private nested type), `ITradeRegistryProvider` (`AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry`), `OrderGroupReconciler _reconciler`.
- Produces: `private async Task RunReconciliationLoop(CancellationToken ct)`, `private async Task ReconcileSession(LiveSessionEntry entry, ITradeRegistryProvider provider, CancellationToken ct)`, `private void LogReconciliationFailure(Exception ex, Guid sessionId, int consecutiveFailures)`.

> This task has no new unit test of its own. Its guard is: the build is clean, the Task 1 tests stay green, AND the **existing** `AlgoTradeForge.LiveHost.Infrastructure.Tests` suite stays green. The change is a behavior-preserving refactor plus the filter correction.

- [ ] **Step 1: Extract the per-session body into `ReconcileSession`**

Add this method to `BinanceLiveConnector` (next to `RunReconciliationLoopAsync`). It is the current Phase 1–3b block verbatim, with the existing comments preserved:

```csharp
private async Task ReconcileSession(
    LiveSessionEntry entry, ITradeRegistryProvider provider, CancellationToken ct)
{
    // Phase 1: Snapshot expected orders on EventQueue (thread-safe read).
    // WriteAsync (not TryWrite): on a bounded queue a full buffer would make
    // TryWrite drop the action, leaving `await tcs.Task` hung forever. The
    // single-reader ProcessingTask drains independently, so WriteAsync always
    // gets a slot and the round-trip completes.
    var tcs = new TaskCompletionSource<IReadOnlyList<ExpectedOrder>>();
    await entry.EventQueue.Writer.WriteAsync(() =>
        tcs.SetResult(provider.TradeRegistry.GetExpectedOrders()), ct);
    var expected = await tcs.Task;

    // Phase 2: Detect on timer thread (exchange query, pure comparison)
    var pendingIds = entry.OrderContext.GetPendingOrders()
        .Select(o => o.Id).Where(id => id > 0).ToHashSet();
    var result = await _reconciler!.DetectAsync(
        entry.PrimaryAsset.Name, expected,
        entry.OrderContext.ResolveExchangeOrderId, pendingIds, ct);

    // Phase 3a: Repair on EventQueue (module mutation serialized)
    if (result.MissingByGroup.Count > 0)
    {
        var repairTcs = new TaskCompletionSource();
        await entry.EventQueue.Writer.WriteAsync(() =>
        {
            foreach (var (groupId, missingIds) in result.MissingByGroup)
                provider.TradeRegistry.RepairGroup(groupId, missingIds);
            repairTcs.SetResult();
        }, ct);
        await repairTcs.Task;
    }

    // Phase 3b: Cancel orphans directly on exchange (no module state)
    if (result.OrphanIds.Count > 0)
        await _reconciler.CancelOrphansAsync(entry.PrimaryAsset.Name, result.OrphanIds, ct);
}
```

- [ ] **Step 2: Add the corrected failure-logging helper**

Add to `BinanceLiveConnector`. Severity is corrected vs. the original (original logged `Error` for the first two failures and *downgraded* to `Warning` at ≥3):

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

- [ ] **Step 3: Replace the loop method**

Replace the entire `RunReconciliationLoopAsync` method (`:445–514`) with this. Keep the top-of-method comment about the in-progress-fill edge case (move it onto `ReconcileSession` if you prefer it next to the code it describes — either is fine, do not delete it):

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

- [ ] **Step 4: Update the single caller**

At `:224`, rename the call:

```csharp
_reconcileTask = RunReconciliationLoop(_cts.Token);
```

- [ ] **Step 5: Verify the loop's edge-case comment survives**

Confirm the "Known edge case: if a fill is in-progress …" comment block from the original method head is still present somewhere on `RunReconciliationLoop` or `ReconcileSession`. It documents a non-obvious duplicate-order interaction and must not be dropped.

- [ ] **Step 6: Build**

Run: `powershell.exe -NoProfile -Command "dotnet build AlgoTradeForge.slnx"`
Expected: **clean build**, 0 errors, 0 new warnings. (Verify no stray reference to `RunReconciliationLoopAsync` remains — it would be an unresolved-symbol error.)

- [ ] **Step 7: Run the full Infrastructure test suite**

Run: `powershell.exe -NoProfile -Command "dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/"`
Expected: **all green**, including the 3 Task 1 tests. (Single `dotnet` process — do not parallelize.)

- [ ] **Step 8: Commit** (controller performs this)

```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceLiveConnector.cs
git commit -F - <<'EOF'
fix(livehost): reconciliation loop survives transient HttpClient timeouts

§D step 2. Rewrite RunReconciliationLoop: inner catch is now
`when (!IsTrueShutdown(ex, ct))`, so an HttpClient.Timeout (an OCE with
ct live) is counted and the loop continues instead of escaping to the
outer handler and silently dying. Extract the 3-phase body into
ReconcileSession, drop the Async suffix on the loop method, and correct
the inverted failure-log severity (>=3 -> Error, else Warning).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_018w22NAfM8bQwp5TTiMMGbX
EOF
```

---

## Self-Review

**Spec coverage:**
- IsTrueShutdown helper (spec §1) → Task 1. ✅
- Extract `ReconcileSession` (spec §2) → Task 2 Step 1. ✅
- Loop reduced + filter corrected + sole outer catch (spec §3) → Task 2 Step 3. ✅
- No-`Async`-suffix rename of loop method + caller (spec §2) → Task 2 Steps 3–4. ✅
- Corrected logging severity (spec §4) → Task 2 Step 2. ✅
- Tests pin `IsTrueShutdown`, 3 cases (spec Testing) → Task 1 Step 1. ✅
- Acceptance: build clean + suite green → Task 2 Steps 6–7. ✅

**Placeholder scan:** No TBD/TODO/"handle edge cases"; every code step shows complete code. ✅

**Type consistency:** `IsTrueShutdown(Exception, CancellationToken)`, `ReconcileSession(LiveSessionEntry, ITradeRegistryProvider, CancellationToken)`, `LogReconciliationFailure(Exception, Guid, int)` — names/types identical across the Interfaces blocks and the code steps. `entry.SessionId` is a `Guid` (matches `_sessions` key type `ConcurrentDictionary<Guid, LiveSessionEntry>`). ✅
