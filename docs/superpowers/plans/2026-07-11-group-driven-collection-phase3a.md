# Group-Driven Collection (Phase 3a) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Collectors, streams, and endpoints take their work from collection groups (via a `CollectionPlan` projection) instead of `HistoryLoaderOptions.Assets[]`; date discovery and instrument precision move to the SQLite index; the reconciler kicks eager backfills with a convergence fingerprint.

**Architecture:** `DesiredStateService` becomes the single pipeline owner: `GroupsChanged`/discovery/backfill-completion → 500 ms debounce → expand groups → build `CollectionPlan` (joins index discovery + instrument meta + recorded disk scale) → evaluate convergence → kick eager missing/partial backfills (fingerprint-guarded) → publish `PlanChanged`. The collector execution chain (`IFeedCollector`, `IArchiveMaterializer`, `SymbolCollector`, `ArchiveBackfillService`, `BackfillOrchestrator`, `ScheduledCollectorService`, stream services) natively consumes `CollectionAsset`/`CollectionFeed`; `AssetCollectionConfig`/`FeedCollectionConfig` die as the runtime model.

**Tech Stack:** C# 14 / .NET 10, ASP.NET Core minimal APIs, Microsoft.Data.Sqlite (history-index.sqlite), xUnit + NSubstitute. No new NuGet packages.

**Spec:** `docs/superpowers/specs/2026-07-11-group-driven-collection-phase3a-design.md` (approved 2026-07-11). Parent: `docs/superpowers/specs/2026-07-10-declarative-data-management-design.md`.

## Global Constraints

- **Branch:** `feat/group-driven-collection-phase3a` off `main` AFTER the phase-2 PR (`feat/collection-groups-phase2`) merges. First commit on the branch adds this plan + the phase-3a spec (`git add docs/superpowers/specs/2026-07-11-group-driven-collection-phase3a-design.md docs/superpowers/plans/2026-07-11-group-driven-collection-phase3a.md`).
- **ONE dotnet process at a time.** Never build/test in parallel. `pwsh` does not exist — use `powershell.exe` if a shell script is unavoidable; prefer plain `dotnet` commands.
- Test suites: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/` (~810 green at branch point). Frontend (Task 12 only): from `frontend/`: `npx tsc --noEmit` and `npm test` (~230 green). Full solution build: `dotnet build AlgoTradeForge.slnx`.
- **No `Async` suffix on new or signature-changed methods** (Constitution v1.8.3). When a task changes an existing `...Async` signature it MUST drop the suffix (e.g. `CollectAsync` → `Collect`). `CancellationToken ct = default` on every async method.
- **No sync-over-async.** Prefer `using var _ = await gate.LockAsync(ct);` over try/finally (Constitution v1.9.1).
- **One type per file** (Constitution v1.9.0) — exception: single-line records accompanying the type they serve may share its file (precedent: `CollectionGroup.cs`, `DesiredState.cs`).
- **Comments:** terse, only for non-obvious behavior. No XML restating identifiers.
- Exchange ids are **lowercase everywhere** in group-derived data (`DesiredTuple.Exchange` is normalized by `GroupExpansion`); index reads get `COLLATE NOCASE` tolerance (Task 1) but writes stay lowercase.
- Convergence status vocabulary after this plan: `unsupported | on-demand | blocked | awaiting-data | missing | partial | materialized`. Rule order: unsupported → blocked → on-demand → awaiting-data → missing → materialized → partial.
- Stream-fed feeds (only live source is a WebSocket): exactly `FeedNames.Liquidations` and `FeedNames.BookTicker`.
- Instrument meta TTL: 24 hours. Debounce: 500 ms (existing). Kick fingerprint resets ONLY on `GroupsChanged`.
- Commits: explicit paths only (never `-A`/`-u`); nothing under `docs/superpowers/**` or `.superpowers/**` in task commits (plan+spec ride in the branch-setup commit only). Trailers on every commit:
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`
  `Claude-Session: https://claude.ai/code/session_01FS9nV12EvhcnTbE3CpUsvc`

---

## File Structure Overview

```
src/AlgoTradeForge.HistoryLoader.Application/
  Index/IHistoryIndex.cs                     # + discovery, instrument-meta, ListAllFeedKeys (Task 1, 2)
  Abstractions/IInstrumentMetaProvider.cs    # NEW (Task 2)
  Abstractions/ISettingsWriter.cs            # DELETED (Task 5)
  Collection/CollectionPlan.cs               # NEW record family (Task 3)
  Collection/ICollectionPlanSource.cs        # NEW + CollectionPlanHolder (Task 3)
  Collection/RecordedScale.cs                # NEW manifest scale reader (Task 3)
  Collection/IFeedCollector.cs               # signature → CollectionAsset/CollectionFeed (Task 4)
  Collection/SymbolCollector.cs              # native model + discovery→index (Tasks 4, 5)
  Collection/BackfillOrchestrator.cs         # native model + shared semaphore (Task 4)
  Collection/CollectionPolicy.cs             # DELETED (Task 4)
  Collection/CollectionChangeNotifier.cs     # NEW discovery event (Task 5)
  Archive/IArchiveMaterializer.cs            # signature change (Task 4)
  Archive/ArchiveBackfillService.cs          # native model + discovery→index (Tasks 4, 5)
  Groups/CollectionPlanBuilder.cs            # NEW pure builder (Task 3)
  Groups/ConvergenceEvaluator.cs             # clamp/blocked/awaiting-data/one-SQL orphans (Task 7)
  Groups/ConvergenceReport.cs                # status vocabulary comment (Task 7)
  Groups/GroupValidator.cs                   # derived-name collision rule (Task 11)
  Canonicalization/CanonicalizerOptions.cs   # maps die (Task 10)
  HistoryLoaderOptions.cs                    # + GapThresholdMultiplier; Assets = importer-only (Tasks 4, 10)
src/AlgoTradeForge.HistoryLoader.Infrastructure/
  Index/HistoryIndexInitializer.cs           # + instrument_meta table (Task 2)
  Index/SqliteHistoryIndex.cs                # new methods + NOCASE (Tasks 1, 2)
  Binance/BinanceInstrumentMetaProvider.cs   # NEW exchangeInfo fetcher (Task 2)
  Binance/TickSizeParser.cs                  # NEW (extracted from BinanceLoadAssetResolver) (Task 2)
  Archive/BinanceLoadAssetResolver.cs        # DELETED (Task 9)
src/AlgoTradeForge.HistoryLoader.WebApi/
  Groups/DesiredStateService.cs              # pipeline owner: plan+evaluate+kick+PlanChanged (Task 6)
  Collection/ScheduledCollectorService.cs    # iterates plan (Task 6)
  Collection/{Liquidation,BookTicker,SpotAggTrade}StreamService.cs  # plan + PlanChanged (Task 8)
  Collection/FundingInfoRefreshService.cs    # plan (Task 8)
  Endpoints/{Load,Backfill,Status}Endpoints.cs   # plan lookups, P5 422 (Task 9)
  Endpoints/AggregationEndpoints.cs          # plan lookups (Task 10)
  AppSettingsWriter.cs                       # DELETED (Task 5)
frontend/…                                   # blocked/awaiting-data chips (Task 12)
```

Execution order: 1 → 2 → 3 → 4 → 5 → 6 → 7 → 8 → 9 → 10 → 11 → 12 → 13. Tasks 4–6 are the load-bearing sequence; nothing may be reordered across them.

---

### Task 1: Index — discovery accessors, ListAllFeedKeys, COLLATE NOCASE

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Index/IHistoryIndex.cs`
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/SqliteHistoryIndex.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Index/SqliteHistoryIndexTests.cs` (existing file — add tests alongside existing contract tests; follow its fixture: `Pooling=False`, `SqliteConnection.ClearAllPools()` in Dispose)

**Interfaces (Produces):**
```csharp
public sealed record DiscoveredFirstMonthRow(string Exchange, string Dir, string FeedName, string Interval, string Month);

// added to IHistoryIndex:
Task SetDiscoveredFirstMonth(string exchange, string dir, string feedName, string interval, string month, CancellationToken ct = default);
Task<IReadOnlyList<DiscoveredFirstMonthRow>> ListDiscoveredFirstMonths(CancellationToken ct = default);
Task<IReadOnlyList<(string Exchange, string Dir, string FeedName, string Interval)>> ListAllFeedKeys(CancellationToken ct = default);
```

- [ ] **Step 1: Write failing contract tests** in `SqliteHistoryIndexTests.cs`:

```csharp
[Fact]
public async Task SetDiscoveredFirstMonth_UpsertsBeforeAnyDataWrite()
{
    await _index.SetDiscoveredFirstMonth("binance", "BTCUSDT_perp", "mark-price", "1h", "2023-05");
    var rows = await _index.ListDiscoveredFirstMonths();
    var row = Assert.Single(rows);
    Assert.Equal(("binance", "BTCUSDT_perp", "mark-price", "1h", "2023-05"),
        (row.Exchange, row.Dir, row.FeedName, row.Interval, row.Month));

    // second write overwrites (rediscovery)
    await _index.SetDiscoveredFirstMonth("binance", "BTCUSDT_perp", "mark-price", "1h", "2023-04");
    Assert.Equal("2023-04", (await _index.ListDiscoveredFirstMonths())[0].Month);
}

[Fact]
public async Task SetDiscoveredFirstMonth_PreservesExistingStatusColumns()
{
    await _index.UpsertFeedStatus(new FeedStatusIndexRow(
        "binance", "BTCUSDT_perp", "funding-rate", "", 1L, 2L, 42, "Healthy", "[]", "[\"2024-01\"]"));
    await _index.SetDiscoveredFirstMonth("binance", "BTCUSDT_perp", "funding-rate", "", "2023-11");
    var status = Assert.Single(await _index.GetFeedStatuses("binance", "BTCUSDT_perp"));
    Assert.Equal(42, status.RecordCount);          // upsert must not blank existing columns
    Assert.Equal("[\"2024-01\"]", status.CompleteMonthsJson);
}

[Fact]
public async Task ListAllFeedKeys_UnionsStatusAndMonthRowsAcrossAssets()
{
    await _index.UpsertFeedStatus(new FeedStatusIndexRow("binance", "A", "funding-rate", "", null, null, 0, "Healthy", "[]", "[]"));
    await _index.ReplaceMonths("binance", "B", "candles", "1h",
        [new MonthPartitionRow("2024-01", 10, 100, "2024-01-31T00:00:00Z")]);
    var keys = await _index.ListAllFeedKeys();
    Assert.Contains(("binance", "A", "funding-rate", ""), keys);
    Assert.Contains(("binance", "B", "candles", "1h"), keys);
}

[Fact]
public async Task Reads_AreCaseInsensitive_OnExchangeAndDir()
{
    await _index.UpsertFeedStatus(new FeedStatusIndexRow("Binance", "BTCUSDT_Perp", "mark-price", "1h", null, null, 0, "Healthy", "[]", "[]"));
    await _index.ReplaceMonths("Binance", "BTCUSDT_Perp", "mark-price", "1h",
        [new MonthPartitionRow("2024-01", 10, 100, "2024-01-31T00:00:00Z")]);
    Assert.Single(await _index.GetFeedStatuses("binance", "btcusdt_perp"));
    Assert.Single(await _index.GetMonths("binance", "btcusdt_perp", "mark-price", "1h"));
    Assert.Single(await _index.ListFeedKeys("binance", "btcusdt_perp"));
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter SqliteHistoryIndexTests -v minimal`
Expected: FAIL — `SetDiscoveredFirstMonth`/`ListDiscoveredFirstMonths`/`ListAllFeedKeys` do not exist (compile error).

- [ ] **Step 3: Implement.** Add the three methods to `IHistoryIndex` (+ the `DiscoveredFirstMonthRow` record at the top of `IHistoryIndex.cs`, beside the existing row records). In `SqliteHistoryIndex`:

```csharp
public async Task SetDiscoveredFirstMonth(string exchange, string dir, string feedName, string interval, string month, CancellationToken ct = default)
{
    await using var conn = await Open(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        INSERT INTO feed_status (exchange, dir, feed_name, interval, discovered_first_month)
        VALUES ($ex, $dir, $feed, $iv, $m)
        ON CONFLICT(exchange, dir, feed_name, interval)
        DO UPDATE SET discovered_first_month = excluded.discovered_first_month
        """;
    cmd.Parameters.AddWithValue("$ex", exchange);
    cmd.Parameters.AddWithValue("$dir", dir);
    cmd.Parameters.AddWithValue("$feed", feedName);
    cmd.Parameters.AddWithValue("$iv", interval);
    cmd.Parameters.AddWithValue("$m", month);
    await cmd.ExecuteNonQueryAsync(ct);
}

public async Task<IReadOnlyList<DiscoveredFirstMonthRow>> ListDiscoveredFirstMonths(CancellationToken ct = default)
{
    await using var conn = await Open(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT exchange, dir, feed_name, interval, discovered_first_month
        FROM feed_status WHERE discovered_first_month IS NOT NULL
        """;
    var rows = new List<DiscoveredFirstMonthRow>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
        rows.Add(new DiscoveredFirstMonthRow(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
    return rows;
}

public async Task<IReadOnlyList<(string Exchange, string Dir, string FeedName, string Interval)>> ListAllFeedKeys(CancellationToken ct = default)
{
    await using var conn = await Open(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT DISTINCT exchange, dir, feed_name, interval FROM (
            SELECT exchange, dir, feed_name, interval FROM feed_status
            UNION
            SELECT exchange, dir, feed_name, interval FROM month_partitions
        )
        """;
    var keys = new List<(string, string, string, string)>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
        keys.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
    return keys;
}
```

NOCASE: in `SqliteHistoryIndex`, on **read** predicates only (`GetFeedStatuses`, `GetMonths`, `ListFeedKeys` — both branches of the UNION), change `exchange = $ex` → `exchange = $ex COLLATE NOCASE` and `dir = $dir` → `dir = $dir COLLATE NOCASE`. Do NOT touch write/delete predicates (`ReplaceMonths` deletes, `PruneFeedData`, `PruneAssetsNotIn`, `RemoveAsset`) — collectors write lowercase consistently and rebuild≡incremental invariant tests pin exact-case writes.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter SqliteHistoryIndexTests -v minimal` → PASS, then the full suite `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/` → all green.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/Index/IHistoryIndex.cs src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/SqliteHistoryIndex.cs tests/AlgoTradeForge.HistoryLoader.Tests/Index/SqliteHistoryIndexTests.cs
git commit -m "feat(index): discovered-first-month accessors, ListAllFeedKeys, NOCASE read predicates"
```

---

### Task 2: Index — `instrument_meta` table + `BinanceInstrumentMetaProvider`

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/HistoryIndexInitializer.cs` (add table to `Schema`)
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Index/IHistoryIndex.cs` + `src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/SqliteHistoryIndex.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Abstractions/IInstrumentMetaProvider.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Binance/TickSizeParser.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Binance/BinanceInstrumentMetaProvider.cs`
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/BinanceLoadAssetResolver.cs` (its private `CountFractionalDigits` moves to `TickSizeParser`; resolver calls the shared parser — resolver itself dies in Task 9)
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/DependencyInjection.cs` (register provider)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Index/SqliteHistoryIndexTests.cs`, new `tests/AlgoTradeForge.HistoryLoader.Tests/Binance/TickSizeParserTests.cs`, new `tests/AlgoTradeForge.HistoryLoader.Tests/Binance/BinanceInstrumentMetaProviderTests.cs`

**Interfaces (Produces):**
```csharp
public sealed record InstrumentMetaRow(string Exchange, string Dir, int PriceDecimals, int QtyDecimals, string TickSize, string FetchedAtUtc);

// added to IHistoryIndex:
Task UpsertInstrumentMeta(IReadOnlyList<InstrumentMetaRow> rows, CancellationToken ct = default);   // batch, one transaction
Task<IReadOnlyList<InstrumentMetaRow>> ListInstrumentMeta(string? exchange = null, CancellationToken ct = default);

// Application/Abstractions/IInstrumentMetaProvider.cs
public interface IInstrumentMetaProvider
{
    /// <summary>Fetches exchangeInfo (spot + futures) and upserts instrument_meta when the last
    /// fetch is older than 24h. In-memory last-fetch timestamps make repeat calls free — a group
    /// symbol absent from a fresh response stays absent (blocked) until the next TTL expiry.</summary>
    Task EnsureFresh(string exchange, CancellationToken ct = default);
}

public static class TickSizeParser
{
    /// <summary>"0.01000000" → 2; "1" → 0; trailing zeros ignored.</summary>
    public static int FractionalDigits(string tickOrStepSize);
}
```

- [ ] **Step 1: Failing tests.**

`TickSizeParserTests.cs`:
```csharp
[Theory]
[InlineData("0.01000000", 2)]
[InlineData("0.10", 1)]
[InlineData("1", 0)]
[InlineData("0.00001", 5)]
[InlineData("100", 0)]
public void FractionalDigits_IgnoresTrailingZeros(string tickSize, int expected) =>
    Assert.Equal(expected, TickSizeParser.FractionalDigits(tickSize));
```

`SqliteHistoryIndexTests.cs` — add:
```csharp
[Fact]
public async Task InstrumentMeta_BatchUpsert_AndFilteredList()
{
    await _index.UpsertInstrumentMeta([
        new InstrumentMetaRow("binance", "BTCUSDT_perp", 1, 3, "0.10", "2026-07-11T00:00:00Z"),
        new InstrumentMetaRow("binance", "ETHUSDT", 2, 4, "0.01", "2026-07-11T00:00:00Z")]);
    Assert.Equal(2, (await _index.ListInstrumentMeta("binance")).Count);

    // re-upsert overwrites in place (PK exchange+dir)
    await _index.UpsertInstrumentMeta([
        new InstrumentMetaRow("binance", "BTCUSDT_perp", 2, 3, "0.01", "2026-07-12T00:00:00Z")]);
    var row = (await _index.ListInstrumentMeta("binance")).Single(r => r.Dir == "BTCUSDT_perp");
    Assert.Equal(2, row.PriceDecimals);
}
```

`BinanceInstrumentMetaProviderTests.cs` — stub `HttpMessageHandler` (follow the existing Binance client test pattern in this test project; if none uses a handler stub, write a minimal `StubHandler : HttpMessageHandler` returning canned JSON per URL). Canned futures exchangeInfo body (trimmed to what the parser needs):
```json
{"symbols":[{"symbol":"BTCUSDT","status":"TRADING",
  "filters":[{"filterType":"PRICE_FILTER","tickSize":"0.10000000"},
             {"filterType":"LOT_SIZE","stepSize":"0.00100000"}]}]}
```
Tests:
```csharp
[Fact] // derives decimals from tickSize/stepSize, NOT pricePrecision, and maps dir via symbology
public async Task EnsureFresh_UpsertsRowsFromPriceAndLotFilters() { /* EnsureFresh("binance");
    assert index.ListInstrumentMeta has BTCUSDT_perp with PriceDecimals=1, QtyDecimals=3 (futures)
    and BTCUSDT with the spot response's decimals */ }

[Fact] // TTL: second call within 24h must not hit HTTP again
public async Task EnsureFresh_WithinTtl_DoesNotRefetch() { /* count handler invocations == 2 (spot+futures) after two EnsureFresh calls */ }
```
Use a real `SqliteHistoryIndex` over a temp DB (same fixture as `SqliteHistoryIndexTests`) — the provider's contract is "rows land in the index".

- [ ] **Step 2: Run to verify failure** — same filter commands; expected compile errors.

- [ ] **Step 3: Implement.**

`HistoryIndexInitializer.Schema` — append (the schema script is idempotent-additive; `EnsureCreated` runs it in full on every boot, so existing DBs gain the table without a version bump — leave `CurrentVersion = 1`):
```sql
CREATE TABLE IF NOT EXISTS instrument_meta (
    exchange       TEXT NOT NULL,
    dir            TEXT NOT NULL,
    price_decimals INTEGER NOT NULL,
    qty_decimals   INTEGER NOT NULL,
    tick_size      TEXT NOT NULL,
    fetched_at     TEXT NOT NULL,
    PRIMARY KEY (exchange, dir)
);
```

`SqliteHistoryIndex.UpsertInstrumentMeta`: one transaction, `INSERT … ON CONFLICT(exchange, dir) DO UPDATE SET price_decimals=excluded.price_decimals, qty_decimals=excluded.qty_decimals, tick_size=excluded.tick_size, fetched_at=excluded.fetched_at`. `ListInstrumentMeta`: `SELECT … FROM instrument_meta` with optional `WHERE exchange = $ex COLLATE NOCASE`.

`TickSizeParser.FractionalDigits` — move the body of `BinanceLoadAssetResolver.CountFractionalDigits` verbatim:
```csharp
public static int FractionalDigits(string tickOrStepSize)
{
    var dotIdx = tickOrStepSize.IndexOf('.');
    if (dotIdx < 0) return 0;
    var fraction = tickOrStepSize.AsSpan(dotIdx + 1);
    var lastNonZero = -1;
    for (var i = 0; i < fraction.Length; i++)
        if (fraction[i] != '0') lastNonZero = i;
    return lastNonZero + 1;
}
```
Update `BinanceLoadAssetResolver` to call it (delete its private copy).

`BinanceInstrumentMetaProvider` (Infrastructure/Binance):
```csharp
/// <summary>Fetches Binance exchangeInfo (one spot + one futures call — each returns EVERY
/// symbol) and upserts instrument_meta. Decimals derive from PRICE_FILTER.tickSize and
/// LOT_SIZE.stepSize — NEVER pricePrecision (futures pricePrecision is the API field width,
/// spot has no precision fields). Dir mapping via SymbologyRegistry conventions: futures
/// symbols get the "_perp" dir suffix (AssetDirectoryName convention), spot symbols the bare
/// API symbol. In-memory last-fetch per venue class enforces the 24h TTL.</summary>
public sealed class BinanceInstrumentMetaProvider(
    IHttpClientFactory httpClientFactory,
    IHistoryIndex index,
    IOptionsMonitor<HistoryLoaderOptions> options,
    TimeProvider clock,
    ILogger<BinanceInstrumentMetaProvider> logger) : IInstrumentMetaProvider
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastFetch = DateTimeOffset.MinValue;

    public async Task EnsureFresh(string exchange, CancellationToken ct = default)
    {
        if (!string.Equals(exchange, "binance", StringComparison.OrdinalIgnoreCase)) return;
        if (clock.GetUtcNow() - _lastFetch < Ttl) return;
        using var _ = await _gate.LockAsync(ct);
        if (clock.GetUtcNow() - _lastFetch < Ttl) return;

        var binance = options.CurrentValue.Binance;
        var fetchedAt = clock.GetUtcNow().UtcDateTime.ToString("O");
        // Upsert per venue class immediately: if the second fetch throws, the first class's rows
        // are already persisted (partial success shrinks the blocked set under degradation).
        await index.UpsertInstrumentMeta(
            await Fetch($"{binance.FuturesBaseUrl}/fapi/v1/exchangeInfo", isFutures: true, fetchedAt, ct), ct);
        await index.UpsertInstrumentMeta(
            await Fetch($"{binance.SpotBaseUrl}/api/v3/exchangeInfo", isFutures: false, fetchedAt, ct), ct);
        _lastFetch = clock.GetUtcNow();
        logger.LogInformation("instrument_meta refreshed");
    }

    private async Task<List<InstrumentMetaRow>> Fetch(string url, bool isFutures, string fetchedAt, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("binance-meta");
        using var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var rows = new List<InstrumentMetaRow>();
        foreach (var sym in doc.RootElement.GetProperty("symbols").EnumerateArray())
        {
            var apiSymbol = sym.GetProperty("symbol").GetString()!;
            string? tickSize = null, stepSize = null;
            foreach (var filter in sym.GetProperty("filters").EnumerateArray())
            {
                var type = filter.GetProperty("filterType").GetString();
                if (type == "PRICE_FILTER") tickSize = filter.GetProperty("tickSize").GetString();
                else if (type == "LOT_SIZE") stepSize = filter.GetProperty("stepSize").GetString();
            }
            if (tickSize is null) continue;
            var dir = isFutures ? $"{apiSymbol}_perp" : apiSymbol;
            rows.Add(new InstrumentMetaRow("binance", dir,
                TickSizeParser.FractionalDigits(tickSize),
                stepSize is null ? 0 : TickSizeParser.FractionalDigits(stepSize),
                tickSize, fetchedAt));
        }
        return rows;
    }
}
```
Note: dir derivation duplicates the `AssetDirectoryName`/`BinanceSymbology` convention on purpose — meta covers ALL venue symbols, most of which no group declares, so there is no canonical symbol to route through the symbology. Add the one-line comment.

DI (`DependencyInjection.cs`): `services.AddSingleton<IInstrumentMetaProvider, BinanceInstrumentMetaProvider>();` and a named HttpClient `"binance-meta"` if the registration pattern requires it (mirror `"binance-archive"`).

- [ ] **Step 4: Run** — filter tests PASS, then full HistoryLoader suite green.
- [ ] **Step 5: Commit** (explicit paths incl. DI file):

```bash
git commit -m "feat(index): instrument_meta table + Binance exchangeInfo provider (tickSize-derived decimals, 24h TTL)"
```

---

### Task 3: `CollectionPlan` records + `CollectionPlanBuilder` + `RecordedScale`

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Collection/CollectionPlan.cs` (record family, one file — `CollectionGroup.cs` precedent)
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Collection/ICollectionPlanSource.cs` (+ `CollectionPlanHolder` in same file — single-line-adjacent exception does NOT apply to the holder: put `CollectionPlanHolder.cs` in its own file)
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Collection/CollectionPlanHolder.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Collection/RecordedScale.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Groups/CollectionPlanBuilder.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Groups/CollectionPlanBuilderTests.cs`, `tests/AlgoTradeForge.HistoryLoader.Tests/Collection/RecordedScaleTests.cs`

**Interfaces (Produces):**
```csharp
// CollectionPlan.cs
public sealed record CollectionFeed(string FeedName, string Interval, string Collect, string Format, DateOnly EffectiveStart);
public sealed record CollectionAsset(string Exchange, string Canonical, VenueInstrument Venue, int DecimalDigits, IReadOnlyList<CollectionFeed> Feeds);
public sealed record BlockedAsset(string Exchange, string Canonical, string Dir, string Reason);   // Dir: evaluator keys the blocked set by (Exchange, Dir)
public sealed record PlanWarning(string Exchange, string Dir, string Message);
public sealed record CollectionPlan(IReadOnlyList<CollectionAsset> Assets, IReadOnlyList<BlockedAsset> Blocked, IReadOnlyList<PlanWarning> Warnings)
{
    public static readonly CollectionPlan Empty = new([], [], []);
}

// ICollectionPlanSource.cs
public interface ICollectionPlanSource
{
    CollectionPlan Current { get; }
    event Action? PlanChanged;
}

// CollectionPlanHolder.cs — volatile field; Publish(plan) sets then raises PlanChanged.
public sealed class CollectionPlanHolder : ICollectionPlanSource
{
    private volatile CollectionPlan _current = CollectionPlan.Empty;
    public CollectionPlan Current => _current;
    public event Action? PlanChanged;
    public void Publish(CollectionPlan plan) { _current = plan; PlanChanged?.Invoke(); }
}

// RecordedScale.cs
public static class RecordedScale
{
    /// <summary>Reads the on-disk candle ScaleFactor (10^digits) from a feeds.json manifest
    /// (assets.manifest_json in the index). Returns false when the manifest has no candle config.</summary>
    public static bool TryGetDecimalDigits(string manifestJson, out int digits);
}

// CollectionPlanBuilder.cs — pure
public static class CollectionPlanBuilder
{
    public static CollectionPlan Build(
        DesiredState state,
        IReadOnlyList<DiscoveredFirstMonthRow> discovered,
        IReadOnlyList<InstrumentMetaRow> meta,
        IReadOnlyDictionary<(string Exchange, string Dir), int> recordedDigits);
}
```

**Builder semantics (implement exactly):**
1. Skip tuples with `Venue is null` (unsupported) and `IsDerived` (materialization is 3b).
2. Group remaining tuples by `(tuple.Exchange, tuple.Venue)`; `Canonical` from any tuple in the group.
3. `DecimalDigits` resolution per asset: `recordedDigits[(exchange, venue.Dir)]` if present (disk wins). Else `meta` row matching `(exchange, venue.Dir)` — comparison `OrdinalIgnoreCase` on both parts — using `PriceDecimals`. Else the asset is **excluded**: emit `BlockedAsset(exchange, canonical, venue.Dir, "instrument precision unknown (exchangeInfo unavailable or symbol absent)")` and no `CollectionAsset`.
4. When BOTH recorded and meta exist and disagree, still use recorded but emit `PlanWarning(exchange, venue.Dir, $"disk scale {recorded} != exchangeInfo {metaDigits} — venue tickSize drifted; disk governs writes")`.
5. `CollectionFeed.EffectiveStart`: parse `tuple.HistoryStart` (`yyyy-MM`, `DateOnly` first-of-month; unparseable → `DateOnly.MinValue` cannot happen post-validator, but guard: fall back to 2017-01-01). Clamp: among `discovered` rows with matching `(exchange, dir)` (OrdinalIgnoreCase) and `FeedName == tuple.FeedName` (any interval), take the **earliest** `Month`; `EffectiveStart = max(historyStart, earliestDiscovered)`. No discovery rows → no clamp.
6. Feed ordering inside an asset and asset ordering in the plan: sorted (`Ordinal` by Exchange, Dir, then FeedName, Interval) — deterministic output for tests and fingerprints.

- [ ] **Step 1: Failing tests.** `CollectionPlanBuilderTests.cs` — build `DesiredState` fixtures by hand (construct `DesiredTuple` records directly; `VenueInstrument("BTCUSDT", AssetTypes.PerpetualFutures, "BTCUSDT_perp")` — use the actual `AssetTypes` constants found in `src/AlgoTradeForge.HistoryLoader.Domain/AssetTypes.cs`):

```csharp
[Fact] public void Build_GroupsTuplesPerVenue_AndSkipsDerivedAndUnsupported() { … 3 tuples same venue → 1 asset with candles+mark-price feeds; derived EqV tuple and Venue=null tuple absent … }
[Fact] public void Build_RecordedScaleWins_AndDivergenceWarns() { … recordedDigits=2, meta PriceDecimals=1 → asset.DecimalDigits==2, one PlanWarning … }
[Fact] public void Build_NoRecordedNoMeta_BlocksAsset() { … no lookups → Assets empty, single BlockedAsset with reason … }
[Fact] public void Build_EffectiveStart_ClampsToEarliestDiscoveredAcrossIntervals() { … historyStart 2020-01, discovered mark-price rows "1h"→2023-05 and "5m"→2023-04 → EffectiveStart 2023-04-01 … }
[Fact] public void Build_NoDiscovery_KeepsHistoryStart() { … EffectiveStart == 2020-01-01 … }
```

`RecordedScaleTests.cs`:
```csharp
[Fact]
public void TryGetDecimalDigits_ReadsCandleScaleFactor()
{
    // shape written by FeedSchemaManager.EnsureCandleConfig — deserialize the SAME model
    // (FeedMetadata/CandleConfig) with the SAME serializer options it uses, don't hand-parse.
    var manifest = /* serialize new FeedMetadata { Candles = new CandleConfig { ScaleFactor = 100m, Intervals = ["1h"] } } with the manifest serializer */;
    Assert.True(RecordedScale.TryGetDecimalDigits(manifest, out var digits));
    Assert.Equal(2, digits);
}
[Fact] public void TryGetDecimalDigits_NoCandleConfig_ReturnsFalse() { … "{}" → false … }
```
Implementation note for `RecordedScale`: deserialize `FeedMetadata` (the model `FeedSchemaManager` writes — locate it next to `ISchemaManager` in Application/Abstractions or Domain; use the same `JsonSerializerOptions` the schema manager uses for feeds.json), read `Candles?.ScaleFactor`, `digits = (int)Math.Round(Math.Log10((double)scaleFactor))`. Wrap deserialization in try/catch(JsonException) → false.

- [ ] **Step 2: Verify failure** — `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter "CollectionPlanBuilderTests|RecordedScaleTests" -v minimal` → compile errors.
- [ ] **Step 3: Implement** the records, holder, `RecordedScale`, and builder per the semantics block above.
- [ ] **Step 4: Run** — filters PASS, full HistoryLoader suite green (nothing else references the new types yet).
- [ ] **Step 5: Commit** — `feat(groups): CollectionPlan projection + builder (disk-wins scale, blocked assets, EffectiveStart clamp)`

---

### Task 4: Native model switch — collector chain consumes `CollectionAsset`/`CollectionFeed`

The mechanical heart of the phase. `AssetCollectionConfig`/`FeedCollectionConfig` disappear from the entire execution chain. **No adapter layer** (owner decision P2).

**Files (Modify):**
- `src/AlgoTradeForge.HistoryLoader.Application/Collection/IFeedCollector.cs`
- `src/AlgoTradeForge.HistoryLoader.Application/Collection/GenericFeedCollectorBase.cs` and **every** `IFeedCollector` implementation under `Application/Collection/Feeds/`
- `src/AlgoTradeForge.HistoryLoader.Application/Archive/IArchiveMaterializer.cs` and every implementation under `Application/Archive/` + `Infrastructure/Archive/` (locate: `grep -l "IArchiveMaterializer" src/`)
- `src/AlgoTradeForge.HistoryLoader.Application/Collection/SymbolCollector.cs`
- `src/AlgoTradeForge.HistoryLoader.Application/Archive/ArchiveBackfillService.cs`
- `src/AlgoTradeForge.HistoryLoader.Application/Collection/BackfillOrchestrator.cs`
- Delete: `src/AlgoTradeForge.HistoryLoader.Application/Collection/CollectionPolicy.cs`
- `src/AlgoTradeForge.HistoryLoader.Application/HistoryLoaderOptions.cs` (add `GapThresholdMultiplier`)
- Tests: every test file referencing the old config types in collector/archive paths (`grep -l "AssetCollectionConfig" tests/`)

**Transformation rules (apply everywhere in the chain):**

| Old | New |
|---|---|
| `AssetCollectionConfig assetConfig` param | `CollectionAsset asset` |
| `FeedCollectionConfig feedConfig` param | `CollectionFeed feed` |
| `assetConfig.Symbol` | `asset.Venue.ApiSymbol` |
| `assetConfig.Type` | `asset.Venue.AssetType` |
| `assetConfig.Exchange` | `asset.Exchange` |
| `assetConfig.DecimalDigits` | `asset.DecimalDigits` |
| `feedConfig.Name` | `feed.FeedName` |
| `feedConfig.Interval` | `feed.Interval` |
| `feedConfig.HistoryStart ?? assetConfig.HistoryStart` | `feed.EffectiveStart` |
| `feedConfig.GapThresholdMultiplier` | `options.CurrentValue.GapThresholdMultiplier` (inject `IOptionsMonitor<HistoryLoaderOptions>` where not already present) |
| `feedConfig.Enabled` filters | delete (absence from plan == disabled) |
| `collectionPolicy.IsEagerlyCollected(asset, feed)` | `feed.Collect == "eager"` |
| `AssetPathConvention.DirectoryName(a.Symbol, a.Type)` in the chain | `asset.Venue.Dir` |
| `BackfillOrchestrator.ResolveAssetDir(dataRoot, asset)` | keep the method, new body: `Path.Combine(dataRoot, asset.Exchange, asset.Venue.Dir)` |

`HistoryLoaderOptions` — add:
```csharp
/// <summary>Gap-detection multiplier for streamed/polled feeds (was per-feed; never overridden).</summary>
public double GapThresholdMultiplier { get; init; } = 2.0;
```

`BackfillOrchestrator` — two changes beyond the type swap:
1. `RunAsync(IReadOnlyList<CollectionAsset> assets, …)` / `TryRunSingleAsync(CollectionAsset asset, …)`.
2. **Shared semaphore** (spec §3.4): replace the per-call `new SemaphoreSlim(config.MaxBackfillConcurrency)` in `RunAsync` with a `readonly` field initialized in the constructor — NOT a lazy `??=` property (boot-sweep kick and a scheduled cycle can hit first access concurrently; each would create its own semaphore and one gets lost, reintroducing the very N×3 the shared gate fixes):
```csharp
private readonly SemaphoreSlim _semaphore = new(options.CurrentValue.MaxBackfillConcurrency);
// MaxBackfillConcurrency is read once at construction — not hot-reloaded.
```

`CollectionPolicy` is **deleted**: the group's `collect` value IS the eager/lazy decision (the legacy replenishable-default inference was applied once by `LegacyGroupImporter` at conversion). Update `ScheduledCollectorService` constructor accordingly — but its `CollectCycleAsync` source switch happens in Task 6; in THIS task change only its parameter types so the solution compiles: `CollectCycleAsync` iterates a `CollectionPlan` passed in… **No.** To keep this task self-contained: give `ScheduledCollectorService` a constructor dependency `ICollectionPlanSource planSource` (registered in DI in this task: `services.AddSingleton<CollectionPlanHolder>(); services.AddSingleton<ICollectionPlanSource>(sp => sp.GetRequiredService<CollectionPlanHolder>());` — the holder starts `Empty`, so collectors are idle until Task 6 wires publishing) and rewrite `CollectCycleAsync`:

```csharp
internal async Task CollectCycleAsync(CancellationToken ct)
{
    var config = options.CurrentValue;
    var consecutiveNetworkFailures = 0;

    foreach (var asset in planSource.Current.Assets)
    {
        if (circuitBreaker.IsTripped) return;
        if (FuturesOnly && !AssetTypes.IsFutures(asset.Venue.AssetType)) continue;

        var assetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, asset);

        foreach (var feedName in CollectedFeedNames)
        {
            foreach (var feed in asset.Feeds.Where(f => f.FeedName == feedName && f.Collect == "eager"))
            {
                try
                {
                    var toMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var fromMs = new DateTimeOffset(
                        feed.EffectiveStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                        .ToUnixTimeMilliseconds();
                    await symbolCollector.CollectFeed(asset, feed, assetDir, fromMs, toMs, ct: ct);
                    consecutiveNetworkFailures = 0;
                }
                /* keep the existing catch cascade (418 trip / network threshold / IsTrueShutdown) verbatim,
                   substituting asset.Venue.ApiSymbol for asset.Symbol in log lines */
            }
        }
    }
}
```

`SymbolCollector.CollectFeedAsync` → rename to `CollectFeed` (signature changes ⇒ suffix drops, Constitution v1.8.3); same for `ArchiveBackfillService.CoverFromArchive` (already suffix-free) and `IFeedCollector.CollectAsync` → `Collect`, `BackfillOrchestrator.RunAsync`/`TryRunSingleAsync` → `Run`/`TryRunSingle`. Update all call sites (`LoadJobWorker`, endpoints — they still compile against the new types by resolving assets via… they don't yet; Tasks 9–10 fix endpoints. **To keep the solution compiling in THIS task**, endpoints that currently build `AssetCollectionConfig` from `options.Assets` get a minimal inline bridge: construct a `CollectionAsset` via a temporary private helper `LegacyAssetBridge.ToCollectionAsset(AssetCollectionConfig a)` placed in `src/AlgoTradeForge.HistoryLoader.WebApi/Collection/LegacyAssetBridge.cs` with a `// TODO(phase3a Task 9/10): delete — endpoints move to ICollectionPlanSource` comment. The bridge maps: `Venue = new VenueInstrument(a.Symbol, a.Type, AssetPathConvention.DirectoryName(a.Symbol, a.Type))`, `Exchange = a.Exchange.ToLowerInvariant()`, `DecimalDigits = a.DecimalDigits`, feeds from `a.Feeds.Where(f => f.Enabled)` with `Collect = f.Eager ? "eager" : "on-demand"`… **feeds bridge detail**: `EffectiveStart = f.HistoryStart ?? a.HistoryStart`, `Format = "csv"`. The bridge is Tasks 9–10's deletion target; it is NOT an adapter for collectors (collectors read the plan), only a stopgap for endpoint call sites — `LoadJobWorker` included: `LoadJob` still carries the resolver-produced `AssetCollectionConfig` until Task 9, so the worker maps it through the bridge at its `TryRunSingle` call site.)

**Test migration:** every collector/archive test constructing `AssetCollectionConfig`/`FeedCollectionConfig` switches to `CollectionAsset`/`CollectionFeed`. Add a shared test factory to keep the diff readable — `tests/AlgoTradeForge.HistoryLoader.Tests/TestData/CollectionAssets.cs`:
```csharp
internal static class CollectionAssets
{
    internal static CollectionAsset Perp(string apiSymbol = "BTCUSDT", int digits = 2,
        params CollectionFeed[] feeds) =>
        new("binance", $"{apiSymbol[..^4]}/USDT-PERP",
            new VenueInstrument(apiSymbol, AssetTypes.PerpetualFutures, $"{apiSymbol}_perp"),
            digits, feeds);
    internal static CollectionFeed Feed(string name, string interval = "", string collect = "eager",
        DateOnly? start = null) =>
        new(name, interval, collect, "csv", start ?? new DateOnly(2024, 1, 1));
}
```
(Verify the exact `AssetTypes` constant names in `Domain/AssetTypes.cs` before use — `IsFutures`/`IsSpot` helpers exist; use whatever constant they test for.)

- [ ] **Step 1:** Change the two interfaces + `SymbolCollector`/`ArchiveBackfillService`/`BackfillOrchestrator`/`ScheduledCollectorService` signatures; delete `CollectionPolicy`; add `GapThresholdMultiplier`; add the bridge. Build: `dotnet build AlgoTradeForge.slnx` — chase every compile error through implementations and tests with the transformation table. This task is compiler-driven; the table is exhaustive for the property mapping.
- [ ] **Step 2:** Migrate tests with the factory; delete `CollectionPolicy` tests; keep the behavioral assertions identical (this task changes types, not behavior — any test asserting behavior that no longer exists, e.g. `Enabled=false` filtering or per-feed gap multiplier, is replaced by its plan-model equivalent: absence-from-plan / global option). Additionally — a small **behavior change** with a red-first test: `FeedSchemaManager.EnsureCandleConfig` currently overwrites `Candles.ScaleFactor` unconditionally (harmless today: the value always came from the same config). Post-3a a `recordedDigits` miss (fresh-rebuild window where the asset is on disk but not yet indexed) can route a divergent exchangeInfo value here and silently corrupt the recorded scale. Change it to create-if-absent — preserve an existing `ScaleFactor`, only intervals may grow — and pin with a test (`EnsureCandleConfig_DoesNotOverwriteExistingScaleFactor`). This, not the best-effort `recordedDigits` warning, is the actual backstop protecting existing CSVs.
- [ ] **Step 3:** Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/` → green. `dotnet build ../AlgoTradeForge.Private/AlgoTradeForge.Full.slnx` → clean (private repo does not reference HistoryLoader internals, expected no-op — verify).
- [ ] **Step 4:** Commit — `refactor(collection)!: collector chain consumes CollectionAsset/CollectionFeed natively (Assets[] leaves the hot path)`

---

### Task 5: Discovery persistence → index (+ notifier); delete `ISettingsWriter`

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Collection/SymbolCollector.cs` (lines around the old `_settingsWriter.UpdateFeedHistoryStart` call)
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Archive/ArchiveBackfillService.cs` (Step-4 discovery block)
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Collection/CollectionChangeNotifier.cs`
- Delete: `src/AlgoTradeForge.HistoryLoader.Application/Abstractions/ISettingsWriter.cs`, `src/AlgoTradeForge.HistoryLoader.WebApi/AppSettingsWriter.cs` (+ its DI registration in `Program.cs`, + its test file)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Collection/SymbolCollectorTests.cs`, `…/Archive/ArchiveBackfillServiceTests.cs` (existing files — replace `ISettingsWriter` substitutes with `IHistoryIndex` + notifier assertions)

**Interfaces (Produces):**
```csharp
/// <summary>Raised after a collector persists a discovered first month — DesiredStateService
/// re-runs the pipeline so CollectionPlan.EffectiveStart catches up without a group edit
/// (spec §3.2: the replacement for the ISettingsWriter+appsettings-hot-reload feedback loop).</summary>
public sealed class CollectionChangeNotifier
{
    public event Action? DiscoveryRecorded;
    public void NotifyDiscoveryRecorded() => DiscoveryRecorded?.Invoke();
}
```
DI: singleton.

**`SymbolCollector`** — constructor swaps `ISettingsWriter settingsWriter` for `IHistoryIndex index, CollectionChangeNotifier notifier`; the persist site becomes:
```csharp
await index.SetDiscoveredFirstMonth(
    asset.Exchange, asset.Venue.Dir,
    feed.FeedName, feed.Interval,
    $"{discoveredDate.Year:D4}-{discoveredDate.Month:D2}", ct);
notifier.NotifyDiscoveryRecorded();
```

**`ArchiveBackfillService`** — same swap; the Step-4 block persists `persistStart` as `yyyy-MM` via `SetDiscoveredFirstMonth(asset.Exchange, asset.Venue.Dir, feed.FeedName, feed.Interval, …)` + `NotifyDiscoveryRecorded()`. Delete the "AppSettingsWriter is a no-op for symbols not in config" comment (no longer true or relevant — the index accepts any asset).

- [ ] **Step 1: Failing tests.** In `SymbolCollectorTests`: the existing date-discovery test (binary search path) now asserts `index.Received().SetDiscoveredFirstMonth("binance", "BTCUSDT_perp", <feed>, <interval>, "<yyyy-MM>")` and that the notifier event fired (subscribe a flag). Same pattern in `ArchiveBackfillServiceTests` for the leading-unavailable discovery path.
- [ ] **Step 2:** Run filters, expect FAIL (constructor mismatch).
- [ ] **Step 3:** Implement; delete `ISettingsWriter`/`AppSettingsWriter` + registration + tests. `grep -r "ISettingsWriter\|AppSettingsWriter" src/ tests/` must return zero hits.
- [ ] **Step 4:** Full HistoryLoader suite green.
- [ ] **Step 5:** Commit — `feat(collection): date discovery persists to index (discovered_first_month); ISettingsWriter dies`

---

### Task 6: Pipeline owner — `DesiredStateService` builds plan, evaluates, kicks (fingerprint), publishes

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Groups/DesiredStateService.cs`
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs` (constructor deps)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Groups/DesiredStateServiceTests.cs` (extend existing)

**Interfaces (Consumes):** `CollectionPlanBuilder.Build`, `CollectionPlanHolder.Publish`, `IInstrumentMetaProvider.EnsureFresh`, `IHistoryIndex.{ListDiscoveredFirstMonths, ListInstrumentMeta, ListAssets}`, `RecordedScale.TryGetDecimalDigits`, `BackfillOrchestrator.{Run, IsRunning}`, `CollectionChangeNotifier.DiscoveryRecorded`, `ConvergenceEvaluator` (Task 7 changes its signature — in THIS task keep calling the existing `Evaluate(groups, ct)` and pass `blocked: []` where needed; Task 7 rewires).

**Pipeline (replaces `ComputeReport`):**
```csharp
private async Task RunPipeline(CancellationToken ct)
{
    var docs = await store.List(ct);
    var groups = docs.Select(d => d.Group).ToList();
    var state = GroupExpansion.Expand(groups, registry);

    var exchanges = state.Tuples.Where(t => t.Venue is not null)
        .Select(t => t.Exchange).Distinct(StringComparer.Ordinal).ToList();
    foreach (var exchange in exchanges)
        await metaProvider.EnsureFresh(exchange, ct);   // no-throw contract: wrap in try/catch, log, continue (stale meta beats no plan)

    var discovered = await index.ListDiscoveredFirstMonths(ct);
    var meta = await index.ListInstrumentMeta(ct: ct);
    var recordedDigits = new Dictionary<(string, string), int>();
    foreach (var assetRow in await index.ListAssets(ct: ct))
        if (RecordedScale.TryGetDecimalDigits(assetRow.ManifestJson, out var digits))
            recordedDigits[(assetRow.Exchange.ToLowerInvariant(), assetRow.Dir)] = digits;

    var plan = CollectionPlanBuilder.Build(state, discovered, meta, recordedDigits);
    foreach (var warning in plan.Warnings)
        logger.LogWarning("plan: {Exchange}/{Dir}: {Message}", warning.Exchange, warning.Dir, warning.Message);

    _report = await evaluator.Evaluate(groups, ct);   // Task 7 switches to Evaluate(state, plan.Blocked, …)

    KickEagerBackfills(plan, _report);

    holder.Publish(plan);   // LAST: consumers must never observe a kick against a stale plan
}
```

**Kick + fingerprint:**
```csharp
private readonly Dictionary<string, string> _kickFingerprints = new();   // key: "{exchange}|{dir}"

private void KickEagerBackfills(CollectionPlan plan, ConvergenceReport? report)
{
    if (report is null) return;

    var byAsset = report.Tuples
        .Where(t => t.Status is "missing" or "partial" && t.Tuple.Collect == "eager" && !t.Tuple.IsDerived && t.Tuple.Venue is not null)
        .GroupBy(t => (t.Tuple.Exchange, t.Tuple.Venue!.Dir));

    var kicks = new List<(CollectionAsset Asset, List<string> Feeds)>();
    foreach (var group in byAsset)
    {
        var fingerprint = string.Join(";", group
            .OrderBy(t => t.Tuple.FeedName, StringComparer.Ordinal).ThenBy(t => t.Tuple.Interval, StringComparer.Ordinal)
            .Select(t => $"{t.Tuple.FeedName}|{t.Tuple.Interval}|{t.MonthsCovered}/{t.MonthsExpected}"));
        var key = $"{group.Key.Exchange}|{group.Key.Dir}";
        if (_kickFingerprints.TryGetValue(key, out var prev) && prev == fingerprint)
            continue;   // unfillable hole / no movement since last kick — spec §3.4
        _kickFingerprints[key] = fingerprint;

        var asset = plan.Assets.FirstOrDefault(a =>
            a.Exchange == group.Key.Exchange && a.Venue.Dir == group.Key.Dir);
        if (asset is null) { _kickFingerprints.Remove(key); continue; }   // blocked/excluded — do NOT retain the fingerprint, or a later plan appearance with unchanged coverage would be suppressed
        kicks.Add((asset, group.Select(t => t.Tuple.FeedName).Distinct().ToList()));
    }
    if (kicks.Count == 0) return;

    _ = Task.Run(async () =>
    {
        try
        {
            foreach (var (asset, feeds) in kicks)
                await runner.Run(asset, feeds, _stopping);   // IEagerBackfillRunner seam (see Step 1) — thin wrapper over BackfillOrchestrator.Run
        }
        catch (Exception ex) { logger.LogError(ex, "kick backfill failed"); }
        finally { Retrigger(); }   // recompute-on-completion closes the loop (spec §3.4)
    }, _stopping);
}
```

**Triggers:** `OnGroupsChanged` additionally clears `_kickFingerprints` (a group edit is a legitimate retry). New private `Retrigger()` = the existing debounced schedule WITHOUT clearing fingerprints; `notifier.DiscoveryRecorded += Retrigger` (subscribe after first compute, unsubscribe in finally, mirroring `GroupsChanged`). The debounce machinery is shared (`OnGroupsChanged` = clear fingerprints + `Retrigger()`).

- [ ] **Step 1: Failing tests** (extend `DesiredStateServiceTests` — it exists with fakes for store/evaluator; add fakes for the new deps; NSubstitute for `IHistoryIndex`, `IInstrumentMetaProvider`, real `CollectionPlanHolder`, real orchestrator? No — orchestrator is concrete; wrap kicks by substituting `BackfillOrchestrator`? It's sealed/concrete: introduce `IBackfillKicker` seam? **Decision: extract the minimal seam** — `DesiredStateService` depends on new interface `IEagerBackfillRunner { Task Run(CollectionAsset asset, IReadOnlyList<string> feeds, CancellationToken ct); }` implemented by a thin `EagerBackfillRunner(BackfillOrchestrator orchestrator)` registered in DI. Tests substitute the interface):

```csharp
[Fact] public async Task Pipeline_PublishesPlan_AfterKick() { … order-of-operations: substitute runner records call; PlanChanged handler asserts holder.Current already the new plan … }
[Fact] public async Task Kick_SkipsAsset_WhenFingerprintUnchanged() { … evaluator returns same missing tuple twice; runner.Received(1) total … }
[Fact] public async Task Kick_Rekicks_AfterGroupsChanged() { … same state, but OnGroupsChanged between passes → runner.Received(2) … }
[Fact] public async Task Kick_Rekicks_WhenCoverageMoves() { … second report has covered+1 → runner.Received(2) … }
[Fact] public async Task DiscoveryRecorded_TriggersRecompute_WithoutClearingFingerprints() { … notifier fires → evaluator re-invoked; runner NOT re-invoked for unchanged fingerprint … }
[Fact] public async Task KickCompletion_TriggersRecompute() { … runner completion → evaluator invoked again (poll with timeout; debounce 500ms — use short poll loop ≤5s) … }
```

- [ ] **Step 2:** Verify failure. **Step 3:** Implement (incl. `IEagerBackfillRunner` + impl + DI). **Step 4:** Full suite green. **Step 5:** Commit — `feat(reconciler): DesiredStateService owns plan pipeline; fingerprint-guarded eager kicks; PlanChanged`

---

### Task 7: Evaluator — discovery clamp, `blocked`, `awaiting-data`, one-SQL orphans

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Groups/ConvergenceEvaluator.cs`, `ConvergenceReport.cs`
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Groups/DesiredStateService.cs` (call `Evaluate(state, plan.Blocked, ct)`)
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/GroupEndpoints.cs` (validate keeps the groups overload)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Groups/ConvergenceEvaluatorTests.cs`

**New evaluator signature:**
```csharp
public Task<ConvergenceReport> Evaluate(IReadOnlyList<CollectionGroup> groups, CancellationToken ct = default);            // kept: expands internally, blocked = []
public Task<ConvergenceReport> Evaluate(DesiredState state, IReadOnlyList<BlockedAsset> blocked, CancellationToken ct = default);
internal Task<ConvergenceReport> Evaluate(DesiredState state, IReadOnlyList<BlockedAsset> blocked, DateOnly nowMonth, CancellationToken ct = default);
```

**Rule changes (order: unsupported → blocked → on-demand → awaiting-data → missing → materialized → partial):**
1. `blocked`: tuple's `(Exchange, Venue.Dir)` ∈ blocked set (build a `HashSet` keyed by exchange+dir — `BlockedAsset` carries Canonical, so ALSO pass dir: **change `BlockedAsset` to include `Dir`**: `BlockedAsset(string Exchange, string Canonical, string Dir, string Reason)` — adjust Task 3 builder/tests accordingly when implementing (Task 3 is earlier; define it with `Dir` from the start — this note is normative for Task 3's implementer).
2. Discovery clamp: before computing `expected`, look up discovery for the tuple: candles → exact `(FeedName, Interval)` row; non-candles → earliest `Month` across rows with matching `FeedName` (any interval). `expected = CountExpectedMonths(Max(historyStart, discovered), nowFirst)` where `Max` compares `yyyy-MM` strings ordinally (safe: fixed format). Fetch once per evaluate: `index.ListDiscoveredFirstMonths(ct)` into a lookup.
3. `awaiting-data`: `tuple.FeedName is FeedNames.Liquidations or FeedNames.BookTicker` AND `covered == 0` → `awaiting-data` (before the missing rule). With observed months: expected counts from `max(historyStart, first observed month)` — first observed = earliest month across the tuple's covered-months set (reuse the union already computed for `covered`).
4. Orphan scan: replace the `ListAssets`×`ListFeedKeys` loop with one `index.ListAllFeedKeys(ct)` call; with Task 1's NOCASE the `ToLowerInvariant` dance stays only as `exLower = key.Exchange.ToLowerInvariant()` for claim-set lookups (claim keys are lowercase). Delete the 3-line feedKeysCache caveat comment (obsolete) and the per-asset cache itself.
5. Update the class summary comment to the new status vocabulary + rule order, and `ConvergenceReport.cs`'s status comment likewise.

- [ ] **Step 1: Failing tests** (extend `ConvergenceEvaluatorTests` — fixtures exist for index fakes):
```csharp
[Fact] public async Task Expected_ClampsToDiscoveredFirstMonth() { … group historyStart 2020-01, discovery 2023-05, covered = all months 2023-05..now → materialized (NOT partial). Without the clamp this is the falsely-green idempotency case — assert expected == covered … }
[Fact] public async Task Blocked_WinsOverMissing() { … blocked list contains the asset → status "blocked" even with 0 covered … }
[Fact] public async Task StreamFeed_ZeroObserved_IsAwaitingData_NotMaterialized() { … liquidations tuple, no rows, historyStart future AND past variants → "awaiting-data" both … }
[Fact] public async Task StreamFeed_WithRows_ExpectedFromFirstObserved() { … liquidations rows 2026-05..2026-07 (now 2026-07), historyStart 2024-01 → materialized … }
[Fact] public async Task OrphanScan_SingleQuery_MatchesOldSemantics() { … seed the exact fixture from OrphanedFeedOnClaimedAsset_IsOrphaned + candle-ext claims; assert identical orphan set; NSubstitute: index.Received(1).ListAllFeedKeys(Arg.Any<CancellationToken>()) and index.DidNotReceive().ListFeedKeys(…) in the orphan phase … }
```
- [ ] **Step 2:** FAIL. **Step 3:** Implement + rewire `DesiredStateService` to `Evaluate(state, plan.Blocked, ct)` (drop the double expansion — service already expanded). **Step 4:** Full suite green. **Step 5:** Commit — `feat(evaluator): discovery clamp, blocked/awaiting-data statuses, single-query orphan scan`

---

### Task 8: Streams + misc services consume the plan, resubscribe on PlanChanged

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Collection/LiquidationStreamService.cs`, `BookTickerStreamService.cs`, `SpotAggTradeStreamService.cs`, `FundingInfoRefreshService.cs`
- Test: existing test files for these services (locate via `grep -l "LiquidationStreamService\|BookTickerStreamService\|SpotAggTradeStreamService\|FundingInfoRefreshService" tests/`)

**Pattern (per service):** inject `ICollectionPlanSource planSource`; replace every `config.Assets` symbol-set/asset-lookup with plan equivalents:
- `BuildEnabledSymbolSet` → futures plan assets whose feeds contain the service's feed with `Collect == "eager"`, keyed by `Venue.ApiSymbol` (`OrdinalIgnoreCase` set, as today).
- `FindAssetConfig(config, symbol)` → `planSource.Current.Assets.FirstOrDefault(a => AssetTypes.IsFutures(a.Venue.AssetType) && string.Equals(a.Venue.ApiSymbol, symbol, StringComparison.OrdinalIgnoreCase))`.
- `EnsureSchemas` iterates plan assets.
- **Resubscribe:** subscribe `planSource.PlanChanged += () => Volatile.Write(ref _planDirty, true);` in `ExecuteAsync` (unsubscribe in finally). Venue-wide streams (liquidations — one socket for all symbols): the read loop checks `_planDirty` each message iteration; when set, clears it and rebuilds the local `enabledSymbols` set — no reconnect needed. Per-symbol-subscription streams (book-ticker, spot aggTrade — verify each service's subscribe mechanics when editing): when `_planDirty` is observed, exit the read loop normally so the outer reconnect loop rebuilds subscriptions from `planSource.Current` — and call `reconnect.Reset()` on that deliberate exit path so `StreamReconnectPolicy` does not count plan-triggered resubscribes as failures (a burst of group saves must not exhaust `MaxReconnectAttempts=10` and kill the service as "unstable").
- `FundingInfoRefreshService`: swap its `config.Assets` iteration for plan assets (futures, feed set per its current filter); no dirty-flag needed (periodic — next tick reads `Current`).

- [ ] **Step 1: Failing tests:** for each service where tests exist, retarget fixtures from options-Assets to a `CollectionPlanHolder` publishing a plan built with the Task 4 test factory. Add one new test (liquidations — the parse path is static, so target the symbol-set builder if it's testable; otherwise test via the service's internal methods marked `internal` + InternalsVisibleTo, matching how existing tests reach `CollectCycleAsync`):
```csharp
[Fact] public void EnabledSymbolSet_ComesFromPlan_EagerLiquidationsFuturesOnly() { … plan with eager liquidations perp + on-demand liquidations perp + eager liquidations spot → set == { first ApiSymbol } … }
```
- [ ] **Step 2:** FAIL. **Step 3:** Implement per pattern. **Step 4:** Full suite green. **Step 5:** Commit — `feat(streams): stream + refresh services read CollectionPlan, resubscribe on PlanChanged`

---

### Task 9: Loads/backfill/status endpoints on the plan; P5 (422 undeclared); resolver dies

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/LoadEndpoints.cs`, `BackfillEndpoints.cs`, `StatusEndpoints.cs`, `src/AlgoTradeForge.HistoryLoader.WebApi/Collection/LoadJobWorker.cs`
- Delete: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/BinanceLoadAssetResolver.cs`, `src/AlgoTradeForge.HistoryLoader.Application/Archive/Jobs/ILoadAssetResolver.cs` (+ DI registration)
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Collection/LegacyAssetBridge.cs` → **delete** if this task removes its last caller (Task 10 removes the rest — delete in whichever task drops the final reference)
- Test: existing endpoint/worker test files (`grep -l "LoadEndpoints\|BinanceLoadAssetResolver\|LoadJobWorker" tests/`)

**Semantics:**
- `POST /loads` (LoadEndpoints): resolve the requested `(exchange, symbol, assetType)` against `planSource.Current.Assets` by `Venue.ApiSymbol` + `Venue.AssetType` (+ exchange, OrdinalIgnoreCase). Not found → `422 { error = "symbol_not_declared", message = "symbol is not declared in any enabled collection group" }` (owner decision P5 — deliberate API behavior change: groups are the only entry point; the old resolver's exchangeInfo synthesis path dies with it). Found → the `LoadJob` carries the `CollectionAsset` (change the job record's asset field type; chase compile errors through `LoadJobWorker` — the worker already calls the Task-4 orchestrator signatures).
- `BackfillEndpoints`/`StatusEndpoints`: the `config.Assets.FirstOrDefault(dirName-match)` lookups become `planSource.Current.Assets.FirstOrDefault(a => string.Equals(a.Venue.Dir, symbol, StringComparison.OrdinalIgnoreCase))`; the status list endpoint iterates plan assets (`asset.Feeds` for feed summaries — fields available on `CollectionFeed`).
- `LoadRequestValidator` keeps its `IsReplenishable` gate (registry-based, unchanged) — it now runs AFTER the declared-symbol check.

- [ ] **Step 1: Failing tests:** endpoint tests — undeclared symbol → 422 `symbol_not_declared`; declared symbol → accepted (202 path unchanged); status endpoint returns plan-derived assets. Retarget fixtures to `CollectionPlanHolder`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement; `grep -r "ILoadAssetResolver\|BinanceLoadAssetResolver" src/ tests/` → zero hits. **Step 4:** Full suite green. **Step 5:** Commit — `feat(api): loads/backfill/status endpoints resolve assets from the plan; undeclared symbols 422 (P5)`

---

### Task 10: Aggregation endpoints, FeedCatalog, canonicalizer, options cleanup — `Assets[]` is importer-only

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/AggregationEndpoints.cs` (both `config.Assets.FirstOrDefault` sites → plan lookup by `(exchange, Venue.Dir)`)
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Catalog/FeedCatalog.cs` (drop any `Assets` reads — keep `DataRoot`/other options; verify with grep what it actually reads)
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Canonicalization/CanonicalizerOptions.cs` — delete `InstrumentAssetDirs` and `InstrumentDecimalDigits`; consumers (locate via `grep -rl "InstrumentAssetDirs\|InstrumentDecimalDigits" src/ tests/`, incl. `TradeProjection`) resolve via `ICollectionPlanSource`: dir = `asset.Venue.Dir`, digits = `asset.DecimalDigits`, matched by `Venue.ApiSymbol` (OrdinalIgnoreCase) **AND venue class**: `AssetTypes.IsFutures(asset.Venue.AssetType)` must agree with the venue class implied by `CanonicalizerOptions.Venue` — BTCUSDT is both a spot asset (dir `BTCUSDT`) and a perp (dir `BTCUSDT_perp`), and a bare ApiSymbol `FirstOrDefault` can silently route a perp stream's live-md into the spot dir (the old `InstrumentAssetDirs` map disambiguated this explicitly). Add a test: plan containing both BTCUSDT spot and perp → perp-venue canonicalizer resolves `BTCUSDT_perp`. Instrument absent from plan → keep the existing fallback behavior (canonical segment scale exps / warn log)
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/HistoryLoaderOptionsValidator.cs` — drop per-asset validation rules (GroupValidator owns them); keep non-asset option validation
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/HistoryLoaderOptions.cs` — `Assets` gets the terse comment: `// Importer input only (LegacyGroupImporter first boot). Runtime consumers read ICollectionPlanSource.`
- Delete: `src/AlgoTradeForge.HistoryLoader.WebApi/Collection/LegacyAssetBridge.cs` (last references die here)
- Test: affected existing test files

- [ ] **Step 1:** Failing/retargeted tests for aggregation endpoint asset resolution (plan-based 404 `asset_not_configured` → keep error code but source from plan: rename code to `asset_not_declared`? **No** — keep `asset_not_configured` wire-compatible; FE matches on it. Note in code: `// wire-compatible error code; "configured" now means "declared in an enabled group"`).
- [ ] **Step 2:** FAIL → **Step 3:** implement. `grep -rn "\.Assets" src/AlgoTradeForge.HistoryLoader.WebApi/ src/AlgoTradeForge.HistoryLoader.Application/ src/AlgoTradeForge.HistoryLoader.Infrastructure/` must show ONLY `LegacyGroupImporter` (+ `HistoryLoaderOptions.Assets` declaration itself, group JSON `d.Group.Assets` hits, and validator remnants if any are non-asset). **Step 4:** Full suite green + `dotnet build AlgoTradeForge.slnx` clean. **Step 5:** Commit — `refactor(config): Assets[] is legacy-importer input only; canonicalizer maps die`

---

### Task 11: Validator — derived name must not collide with declarable feed names

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Groups/GroupValidator.cs` (`ValidateDerived`)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Groups/GroupValidatorTests.cs`

- [ ] **Step 1: Failing test:**
```csharp
[Fact]
public void Derived_NameCollidingWithDeclarableFeed_IsError()
{
    var group = ValidGroup() with { Derived = new Dictionary<string, GroupDerived>
        { ["mark-price"] = new GroupDerived { Source = "candles", Type = "EqV", Materialize = "on-demand" } } };
    Assert.Contains(GroupValidator.Validate(group),
        e => e.Contains("derived 'mark-price'") && e.Contains("collides"));
}
```
(Adapt `GroupDerived` construction to the actual record shape in `CollectionGroup.cs`; reuse the test file's existing `ValidGroup()` helper.)
- [ ] **Step 2:** FAIL. **Step 3:** In `ValidateDerived`, first check per derived key:
```csharp
if (DeclarableFeeds.All.Contains(derivedKey))
    errors.Add($"derived '{derivedKey}' collides with a collectable feed name; derived ids must be distinct (post-F1 coverage matching claims feed names across intervals)");
```
- [ ] **Step 4:** Suite green. **Step 5:** Commit — `feat(groups): reject derived names colliding with declarable feeds`

---

### Task 12: Frontend — `blocked` and `awaiting-data` chips

**Files:**
- Modify: `frontend/types/data-tab.ts` (extend the convergence status union: `'unsupported' | 'on-demand' | 'blocked' | 'awaiting-data' | 'missing' | 'partial' | 'materialized'`)
- Modify: the status-chip rendering component(s) under `frontend/components/features/data/groups/` (locate the existing chip color map — group-card and/or desired-state view): `blocked` → error styling (same family as missing but distinct label "blocked"), `awaiting-data` → warning styling, neither counted in any "missing" aggregate the cards compute
- Test: co-located component tests (extend the existing chip/status tests; `npm test` from `frontend/`)

- [ ] **Step 1:** Failing test: status mapping renders `blocked` with error class + `awaiting-data` with warning class; the group-card convergence summary excludes both from its missing count.
- [ ] **Step 2:** `npm test` → FAIL. **Step 3:** Implement. **Step 4:** `npx tsc --noEmit` clean + `npm test` green. **Step 5:** Commit — `fe(data): blocked + awaiting-data convergence chips`

---

### Task 13: Full regression + wrap-up

- [ ] `dotnet build AlgoTradeForge.slnx` → clean.
- [ ] `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/` → green (record final count in the ledger).
- [ ] `dotnet test tests/AlgoTradeForge.Domain.Tests/`, `tests/AlgoTradeForge.Application.Tests/`, `tests/AlgoTradeForge.Infrastructure.Tests/`, `tests/AlgoTradeForge.WebApi.Tests/` — sequentially, all green (HistoryLoader types leak nowhere, but the solution builds as one graph).
- [ ] `dotnet build ../AlgoTradeForge.Private/AlgoTradeForge.Full.slnx` → clean.
- [ ] From `frontend/`: `npx tsc --noEmit` + `npm test` → green.
- [ ] Grep gates: `grep -rn "AssetCollectionConfig\|FeedCollectionConfig" src/ --include="*.cs"` → hits only in `HistoryLoaderOptions.cs` + `LegacyGroupImporter.cs` (+ their tests). `grep -rn "ISettingsWriter\|CollectionPolicy\|LegacyAssetBridge\|ILoadAssetResolver" src/ tests/` → zero.
- [ ] No commit from this task unless gates found strays (fix + commit with explicit paths).

**Live smoke (controller, not a subagent — needs Andrew's env):** fresh HistoryLoader on `HistoryTest` (ports per `reference_vscode_launch`): (1) boot → instrument_meta populated (2 exchangeInfo calls in logs); (2) add a NEW symbol to a group → kick starts collection without manual backfill, scale seeded from exchangeInfo; (3) after kicked backfill completes → convergence recomputes by itself; (4) an unfillable tuple (delisted/typo'd symbol with meta present) kicks once, second recompute skips (log line); (5) `POST /loads` for undeclared symbol → 422; (6) liquidations tuple with no rows → `awaiting-data` chip in FE.

---

## Self-Review Notes (spec ↔ plan)

- §3.1 CollectionPlan/one-pipeline/PlanChanged-last → Tasks 3, 6. Dead config (`Enabled`, per-feed gap multiplier) → Task 4. Derived/unsupported excluded → Task 3 builder rule 1.
- §3.2 discovery→index both writers + notifier feedback → Task 5; evaluator clamp → Task 7; EffectiveStart → Task 3 rule 5.
- §3.3 instrument_meta/tickSize/disk-wins-loudly/TTL-negative-cache/refusal-retryable-blocked → Tasks 2, 3, 7.
- §3.4 kick/shared-semaphore/recompute-on-completion/boot-sweep(first compute kicks — inherent)/fingerprint → Tasks 4 (semaphore), 6.
- §3.5 streams PlanChanged → Task 8. §3.6 Assets death map + P5 → Tasks 9, 10. §3.7 fast-follows → Tasks 1 (NOCASE), 7 (orphans, statuses), 11 (validator).
- §4 testing bullets each map to a named test in Tasks 1–8; live smoke in Task 13.
- Type-consistency: `BlockedAsset` carries `Dir` (defined in Task 3, normative note repeated in Task 7); method renames (`Collect`, `CollectFeed`, `Run`, `TryRunSingle`) introduced once in Task 4 and used by later tasks.
