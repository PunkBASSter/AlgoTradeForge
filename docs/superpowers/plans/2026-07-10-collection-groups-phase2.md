# Collection Groups + Canonical Symbology + Dry-Run Reconciler (Phase 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Named collection-group JSON declarations (desired state) with canonical symbol grammar and per-exchange symbology, a CAS-backed group store, expansion/merge into desired tuples, a legacy appsettings import, a dry-run reconciler exposing convergence status against the phase-1 index, and the Groups UI zone.

**Architecture:** Groups are user-owned JSON files in `{ConfigRoot}/groups/*.json` (spec/status separation — machine never writes them; CAS via `IFileStorage.WriteIfMatch`). Pure functions expand enabled groups into desired `(exchange, canonicalSymbol, feed)` tuples with deterministic merge; per-exchange `IExchangeSymbology` resolves canonical symbols to venue API symbols + on-disk dirs. A dry-run evaluator diffs desired tuples against `IHistoryIndex` (phase 1) and serves convergence statuses; it drives NO collection yet (phase 3). The FE gets a Groups zone: cards + CodeMirror JSON editor + server-side validation preview.

**Tech Stack:** C# 14 / .NET 10, ASP.NET Core minimal APIs, existing `AlgoTradeForge.Storage` (ETag CAS), phase-1 `IHistoryIndex`; TypeScript 5 strict, Next.js 16, TanStack Query, CodeMirror 6 (already in FE deps).

**Spec:** `docs/superpowers/specs/2026-07-10-declarative-data-management-design.md` §3.1, §3.2, §3.4 (dry-run subset), §3.6 zone 1, §3.7. **Depends on phase 1** (branch `feat/history-index-phase1`, PR pending): `IHistoryIndex`, `history-index.sqlite`, `MonthCoverageMath`. Branch off `main` once phase 1 is merged; if not yet merged, branch off `feat/history-index-phase1` and rebase after its squash-merge.

**Phase-2 scope notes (conscious deviations, do not "fix"):**
- Reconciler is DRY-RUN: computes and serves statuses, never enqueues collection work. Status vocabulary here is the subset `unsupported | on-demand | missing | partial | materialized | orphaned`; `materializing`/`up-to-date`/`stale` arrive in phase 3 with job-driving. `on-demand` = tuple with `collect: on-demand` and zero coverage — an EXPECTED state, not a discrepancy; it must never be counted as "missing" anywhere (evaluator or FE).
- Derived tuples cannot converge in phase 2 (no materialize endpoint until phase 3): an unmaterialized derived tuple shows `on-demand` via `DesiredTuple.IsDerived`, regardless of its `materialize` value — never alarm-red in the FE. Documented, do not "fix".
- Convergence month-counting is COARSE: a month counts as covered if a `month_partitions` row exists (interval feeds) or is in `CompleteMonths` (interval-less). Exact `MonthCoverageMath` gap-credit refinement is a phase-3 nicety — do not wire gaps into the evaluator now.
- `discovered_first_month` is still unpopulated; expected-month ranges start at the group's `historyStart` clamped to now. Pre-listing altcoins will show inflated "missing" counts until phase 3 wires discovery into the index — acceptable, documented.
- Dated futures (`-FUT-`) parse successfully but resolve to `unsupported` on Binance (no collector); options suffix `-OPT-` is a reserved parse error.

## Global Constraints

- **ONE dotnet process at a time.** Never run build/test in parallel (CLAUDE.md). Frontend `npm`/`npx` commands run from `frontend/`.
- Shell: Windows PowerShell 5.1 (`powershell.exe`, no `pwsh`); Bash tool available. No `&&` in PowerShell 5.1.
- Build `dotnet build AlgoTradeForge.slnx`; backend tests `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/` (+ `tests/AlgoTradeForge.WebApi.Tests/` when proxy changes); FE `npx tsc --noEmit`, `npm run lint`, `npm test` from `frontend/`.
- **No `Async` suffix** on new async methods; `CancellationToken ct = default` everywhere (Constitution v1.8.3). No sync-over-async.
- **One type per file** (Constitution v1.9.0); single-line records may accompany their interface. Comments terse, non-obvious facts only (v1.8.4). `using` over try/finally (v1.9.1).
- Background loops: never `catch when (ex is not OperationCanceledException)`; use OCE-when-token → return, then generic catch+log.
- xUnit v3 realities (established in phase 1): explicit `using Xunit;`; xUnit1051 is ERROR — pass `TestContext.Current.CancellationToken` via a `static CancellationToken Ct` property; `IAsyncLifetime` members return `ValueTask`. SQLite test fixtures: `Pooling=False` + `SqliteConnection.ClearAllPools()` in Dispose (see `tests/AlgoTradeForge.HistoryLoader.Tests/Index/*` for patterns).
- API wire JSON is snake_case globally, but anonymous-object property names pass through verbatim — spell snake_case explicitly in anonymous objects (existing endpoint style). Group FILE JSON is camelCase (`ManifestJson.Options` family — but groups get their own options, see Task 3).
- **Git:** branch `feat/collection-groups-phase2`. Commit only each task's files, listed explicitly — never `git add -A`/`-u`, never stage `docs/superpowers/**` or `.superpowers/**` (controller commits docs separately). Trailers on every commit:
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>` + the controller-provided `Claude-Session:` line.
- Machine NEVER writes group JSON (spec/status separation) — any code path that would mutate a group file outside the user-driven PUT endpoint is a defect.
- Failing pre-existing tests are fixed in the same task that broke them; never deferred as "pre-existing".

---

### Task 1: Canonical symbol grammar — model + parser (pure)

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Domain/Symbology/CanonicalSymbol.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Domain/Symbology/InstrumentKind.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Domain/Symbology/CanonicalSymbolParser.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Symbology/CanonicalSymbolParserTests.cs`

**Interfaces:**
- Produces:

```csharp
public enum InstrumentKind { Spot, Perpetual, DatedFuture }

public sealed record CanonicalSymbol(string Base, string Quote, InstrumentKind Kind, string? Expiry)
{
    public override string ToString() => Kind switch
    {
        InstrumentKind.Spot => $"{Base}/{Quote}",
        InstrumentKind.Perpetual => $"{Base}/{Quote}-PERP",
        InstrumentKind.DatedFuture => $"{Base}/{Quote}-FUT-{Expiry}",
        _ => throw new InvalidOperationException(),
    };
}

public static class CanonicalSymbolParser
{
    /// <summary>Grammar: BASE/QUOTE | BASE/QUOTE-PERP | BASE/QUOTE-FUT-YYYY-MM. "-OPT-" is a
    /// reserved suffix → explicit error. Tokens: [A-Z0-9]{1,20}. Round-trip: Format(Parse(s)) == s.</summary>
    public static bool TryParse(string input, out CanonicalSymbol? symbol, out string? error);
}
```

Grammar rules (exhaustive): split on first `/` → Base, rest. Rest splits on first `-` → Quote and suffix chain. No suffix → Spot. `PERP` → Perpetual (nothing may follow). `FUT-YYYY-MM` → DatedFuture with Expiry validated as a real year-month (`2000-01`..`2099-12`). Any suffix starting `OPT` → `error = "options instruments are reserved, not yet supported"`. Anything else (lowercase, empty tokens, unknown suffix, trailing garbage, missing `/`) → specific error strings. Wildcard expiries (`202X-09`) are NOT parsed — deferred per spec §6.

- [ ] **Step 1: Failing tests.** Table-driven happy paths (`BTC/USDT`, `BTC/USDT-PERP`, `BTC/USD-FUT-2026-09` incl. round-trip `ToString()`), rejections (`btc/usdt`, `BTC-USDT`, `BTC/USDT-PERP-X`, `BTC/USD-FUT-2026-13`, `BTC/USD-FUT-202X-09`, `BTC/USD-OPT-2026-09-60000-C` with the reserved-error assertion, empty, `/USDT`, `BTC/`). ~14 `[Theory]` cases; assert `error` content on the OPT case only.
- [ ] **Step 2:** Run `--filter "FullyQualifiedName~CanonicalSymbolParserTests"` → compile error.
- [ ] **Step 3:** Implement (pure string parsing, no regex needed; keep it a single readable method + small token validator).
- [ ] **Step 4:** Green on filter; run full HistoryLoader suite.
- [ ] **Step 5:** Commit the 4 files: `feat(groups): canonical symbol grammar + parser`.

---

### Task 2: `IExchangeSymbology` + `BinanceSymbology` + registry

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Domain/Symbology/IExchangeSymbology.cs` (+ single-line `VenueInstrument` record in same file)
- Create: `src/AlgoTradeForge.HistoryLoader.Domain/Symbology/BinanceSymbology.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Domain/Symbology/SymbologyRegistry.cs`
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs` (DI: registry singleton)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Symbology/BinanceSymbologyTests.cs`

**Interfaces:**
- Consumes: `CanonicalSymbol` (Task 1); `AssetTypes` (`"spot" | "perpetual" | "future" | "equity"`); `AssetPathConvention.DirectoryName(symbol, assetType)` (existing — BTCUSDT+perpetual → `BTCUSDT_perp`).
- Produces:

```csharp
/// <summary>Venue resolution of a canonical symbol. ApiSymbol = exchange REST/WS symbol;
/// AssetType = AssetTypes vocabulary; Dir = on-disk asset directory name.</summary>
public sealed record VenueInstrument(string ApiSymbol, string AssetType, string Dir);

public interface IExchangeSymbology
{
    string Exchange { get; }                       // canonical lowercase id, e.g. "binance"
    /// <summary>Override (from group symbolOverrides) is consulted by the CALLER before this method.</summary>
    bool TryResolve(CanonicalSymbol symbol, out VenueInstrument? instrument, out string? unsupportedReason);
}

public sealed class SymbologyRegistry(IEnumerable<IExchangeSymbology> symbologies)
{
    public IExchangeSymbology? Get(string exchange);   // OrdinalIgnoreCase lookup
}
```

`BinanceSymbology`: Spot → `ApiSymbol = Base+Quote`, `AssetType = AssetTypes.Spot`, `Dir = AssetPathConvention.DirectoryName(ApiSymbol, AssetTypes.Spot)`; Perpetual → same ApiSymbol, `AssetTypes.Perpetual`, dir gets `_perp`; DatedFuture → `unsupportedReason = "dated futures are not collectable on binance yet"`. This formalizes the phase-2 rule from archive-backfill: display names never used as API keys.

- [ ] **Step 1: Failing tests.** BTC/USDT → (BTCUSDT, spot, BTCUSDT); BTC/USDT-PERP → (BTCUSDT, perpetual, BTCUSDT_perp); FUT → unsupported with reason; registry case-insensitive get + null for unknown exchange.
- [ ] **Step 2:** Red run. **Step 3:** Implement. **Step 4:** Green + full suite. **Step 5:** Commit: `feat(groups): per-exchange symbology (binance) + registry`.

---

### Task 3: Collection-group model + single-group validation (pure)

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Groups/CollectionGroup.cs` (record + nested single-line records allowed here: `GroupAssets`, `GroupFeed`, `GroupDerived` — they are the group document's shape, one file is the deliberate exception)
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Groups/GroupJson.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Groups/GroupValidator.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Groups/GroupValidatorTests.cs`

**Interfaces:**

```csharp
public sealed record CollectionGroup(
    string Name,
    bool Enabled,
    IReadOnlyList<string> Exchanges,
    GroupAssets Assets,
    IReadOnlyDictionary<string, GroupFeed> Feeds,
    IReadOnlyDictionary<string, GroupDerived>? Derived,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? SymbolOverrides);

public sealed record GroupAssets(IReadOnlyList<string> Symbols, string HistoryStart);          // "yyyy-MM"
public sealed record GroupFeed(string Collect, IReadOnlyList<string>? Intervals, string? Format); // collect: eager|on-demand; format: csv|parquet (null→csv)
public sealed record GroupDerived(string Source, string? Type, string? Threshold, string? SourceInterval, string Materialize); // materialize: eager|on-demand

public static class GroupJson   // camelCase, ignore-null-when-writing; used for FILES only
{
    public static readonly JsonSerializerOptions Options;
}

public static class GroupValidator
{
    /// <summary>Structural validation of ONE group: name regex ^[a-z0-9][a-z0-9_-]{0,63}$ (and name
    /// must equal the file name, enforced by the store); non-empty exchanges/symbols; every exchange
    /// entry must be lowercase (error names the offender — canonical exchange ids are lowercase,
    /// see IExchangeSymbology.Exchange); every symbol parses canonically (errors carry the symbol);
    /// historyStart is yyyy-MM; feed keys ∈ the DeclarableFeeds allow-list ("candles" requires
    /// non-empty Intervals, others must not set Intervals); enum domains explicit: collect ∈ {eager, on-demand}, materialize ∈ {eager, on-demand},
    /// format ∈ {csv, parquet}; derived.source references a key in feeds or "candles";
    /// symbolOverrides keys ⊆ exchanges, override keys parse canonically.
    /// Returns all errors, not first-only.</summary>
    public static IReadOnlyList<string> Validate(CollectionGroup group);
}
```

Feed-name allow-list — hardcode as `DeclarableFeeds` (a `FrozenSet<string>` beside the validator, terse comment pointing at `FeedNames`), EXACTLY these 13: `candles`, `funding-rate`, `mark-price`, `premium-index`, `index-price`, `open-interest`, `taker-volume`, `ls-ratio-global`, `ls-ratio-top-accounts`, `ls-ratio-top-positions`, `liquidations`, `ticks`, `book-ticker`. NOT declarable (each rejected with its own explicit error, not the generic unknown-key one): `candle-ext` — "candle-ext is a side-output written alongside candles, declare candles intervals instead" (phase-1 KeepFeeds lesson: a declared candle-ext tuple would never match the per-interval index rows); `_session` (`FeedNames.Session`) — internal marker, never a collectable feed. Any other key → error naming it.

- [ ] Steps: failing tests (valid group passes; each rule violated once → exact error listed; multi-error group returns all), red, implement, green + full suite, commit: `feat(groups): CollectionGroup model + structural validator`.

---

### Task 4: `GroupStore` — CAS file store over `IFileStorage`

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Groups/IGroupStore.cs` (+ single-line `GroupDocument(CollectionGroup Group, string ETag)` record)
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Groups/GroupStore.cs`
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/HistoryLoaderOptions.cs` (add `public string? ConfigRoot { get; init; }`)
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs` (DI singleton)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Groups/GroupStoreTests.cs`

**Interfaces:**

```csharp
public interface IGroupStore
{
    Task<IReadOnlyList<GroupDocument>> List(CancellationToken ct = default);          // snapshot: all parseable groups
    Task<GroupDocument?> Get(string name, CancellationToken ct = default);
    /// <summary>CAS write. expectedETag null = create (must not exist). Validates name+structure
    /// (GroupValidator) BEFORE writing; throws GroupValidationException(errors) on failure and
    /// lets ConcurrencyConflictException from WriteIfMatch propagate. Returns new ETag.</summary>
    Task<string> Put(string name, CollectionGroup group, string? expectedETag, CancellationToken ct = default);
    Task<bool> Delete(string name, CancellationToken ct = default);
    /// <summary>Fires after every successful Put/Delete. Reconciler subscribes with debounce.
    /// Payload is deliberately empty — subscribers recompute over ALL groups; do not add args.</summary>
    event Action GroupsChanged;
}
```

Implementation notes: root = `options.ConfigRoot ?? Path.Combine(LOCALAPPDATA, "AlgoTradeForge", "HistoryConfig")`; files at `{root}/groups/{name}.json`; name regex enforced on every public method (path-traversal guard, same class of guard as `FeedCatalog.TryResolveAssetDir`); `List` = `IFileStorage.ListKeys(root/groups/, suffix: ".json", recursive: false)` + `ReadWithEtag` each + deserialize with `GroupJson.Options` (unparseable file → skip + `ILogger` warning, NEVER throw from List); `Get`/`Put` use `ReadWithEtag`/`WriteIfMatch` (the `FeedSchemaManager` CAS pattern); group's `Name` field must equal the file name on Put (error otherwise). `GroupValidationException : Exception` carrying `IReadOnlyList<string> Errors` lives beside the store (own file).

- [ ] Steps: failing tests over real `LocalFileStorage` + temp dir (create/read round-trip incl. ETag change; stale-ETag Put → ConcurrencyConflictException; create-when-exists (null etag) → conflict; invalid group → GroupValidationException with errors, file NOT written; name/file mismatch rejected; List skips a corrupt file and returns the healthy ones; GroupsChanged fires on Put and Delete), red, implement, green + full suite, commit: `feat(groups): CAS-backed group store over IFileStorage`.

---

### Task 5: Expansion + merge into desired tuples (pure — the correctness heart)

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Groups/DesiredTuple.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Groups/DesiredState.cs` (single-line result records may share: `GroupConflict`, `UnsupportedTuple`)
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Groups/GroupExpansion.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Groups/GroupExpansionTests.cs`

**Interfaces:**

```csharp
/// <summary>One desired physical feed. FeedKey = (FeedName, Interval) matches the index vocabulary:
/// candles carry one row per interval; interval-less feeds use "". Derived feeds carry their
/// derived id as FeedName (e.g. "EqV_1m_1k") — phase 3 materializes them.</summary>
public sealed record DesiredTuple(
    string Exchange, string Canonical, VenueInstrument? Venue,
    string FeedName, string Interval,
    string Collect,            // eager | on-demand (derived: materialize value)
    string Format,             // csv | parquet
    string HistoryStart,       // yyyy-MM (min across groups)
    bool IsDerived,            // interval-less collected feeds also have Interval == "" — this flag is the ONLY derived marker
    IReadOnlyList<string> Groups);   // contributing group names, for diagnostics

public sealed record GroupConflict(string Key, string Kind, IReadOnlyList<string> Groups, string Message); // Kind: format | derived-definition
public sealed record UnsupportedTuple(string Exchange, string Canonical, string Reason);

public sealed record DesiredState(
    IReadOnlyList<DesiredTuple> Tuples,
    IReadOnlyList<UnsupportedTuple> Unsupported,
    IReadOnlyList<GroupConflict> Conflicts);

public static class GroupExpansion
{
    /// <summary>Pure. Normalizes every exchange id to lowercase ON ENTRY (ToLowerInvariant —
    /// belt-and-suspenders behind the validator's lowercase rule; DesiredTuple.Exchange is always
    /// lowercase, so all downstream comparisons are plain Ordinal). Expands enabled groups ×
    /// exchanges × symbols × feeds (+derived), resolves symbology (overrides first, then registry;
    /// unknown exchange → unsupported "no symbology"), merges duplicates deterministically: eager
    /// beats on-demand, historyStart = min, groups accumulated sorted. Non-mergeable conflicts
    /// (same physical feed different format; same derived name different source/type/threshold)
    /// land in Conflicts — expansion still returns the rest. Unsupported deduped by
    /// (Exchange, Canonical). Disabled groups contribute nothing.</summary>
    public static DesiredState Expand(IReadOnlyList<CollectionGroup> groups, SymbologyRegistry registry);
}
```

Merge key: `(Exchange, Venue.Dir ?? Canonical, FeedName, Interval)` ordinal — safe as plain Ordinal ONLY because Exchange is lowercase-normalized on entry (see doc above). Candles expand to one tuple per interval from `GroupFeed.Intervals`. Derived tuples: `FeedName` = derived id, `Interval` = "", `Format` from source feed's format? No — derived alt-bars are always csv in phase 2 (state it in a terse comment). `symbolOverrides` replace `Venue.ApiSymbol` only (dir/type still from symbology).

- [ ] Steps: failing tests — the decision table as `[Fact]`s: (a) 2 exchanges × 2 symbols × candles[1m,1h]+funding → 2·2·3 tuples; (b) overlap eager-beats-on-demand + min historyStart + both group names recorded; (c) format conflict → GroupConflict(kind=format) naming both groups, conflicting tuple excluded, siblings kept; (d) derived same-name different-threshold → conflict; identical definition → merged silently, merged tuple carries `IsDerived == true` (collected tuples always false); (e) FUT symbol → UnsupportedTuple with binance reason; unknown exchange → unsupported "no symbology for exchange"; (f) disabled group ignored; (g) symbolOverrides swaps ApiSymbol only; (h) two groups declaring `"BINANCE"` and `"binance"` for the same symbol+feed → ONE merged tuple with `Exchange == "binance"` (the case-normalization guarantee — without it these would be two desired copies of one physical feed); (i) the same FUT symbol unsupported in two groups → ONE UnsupportedTuple. Red → implement → green + full suite. Commit: `feat(groups): desired-state expansion + deterministic merge`.

---

### Task 6: Legacy appsettings import (one-shot, startup)

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Groups/LegacyGroupImporter.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.WebApi/Groups/LegacyImportService.cs` (hosted, runs once before reconciler)
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Groups/LegacyGroupImporterTests.cs`

**Interfaces:**

```csharp
public static class LegacyGroupImporter
{
    /// <summary>Pure. Converts HistoryLoaderOptions.Assets into groups named
    /// "legacy-{exchange}-{spot|perp}" (one per exchange×market present): symbols → canonical
    /// strings via reverse Binance mapping (BTCUSDT+perpetual → BTC/USDT-PERP: quote = longest
    /// match from [USDT,USDC,BUSD,BTC,ETH,BNB,FDUSD,TUSD] suffix — terse comment; unmappable
    /// symbol → skipped + returned in warnings); feeds with FeedCollectionConfig.Enabled == false
    /// are SKIPPED (not imported as on-demand — a disabled feed is not a desired feed); enabled
    /// feeds → GroupFeed(collect: Eager||!replenishable ? "eager" : "on-demand", intervals for
    /// candles from FeedCollectionConfig.Interval), historyStart = min asset HistoryStart
    /// formatted yyyy-MM. Lossy by design: DecimalDigits, GapThresholdMultiplier and per-feed
    /// HistoryStart overrides have no group representation — harmless in phase 2 (collectors
    /// still read appsettings; groups drive nothing yet); phase 3 must decide where
    /// decimalDigits comes from when appsettings retire.</summary>
    public static (IReadOnlyList<CollectionGroup> Groups, IReadOnlyList<string> Warnings) Convert(
        HistoryLoaderOptions options, ArchiveMaterializerRegistry replenishables);
}
```

`LegacyImportService` (BackgroundService, ordered in Program.cs BEFORE the reconciler service of Task 8): if `groups/` dir has NO files AND `options.Assets` non-empty → convert, `Put` each with `expectedETag: null`, log group names + warnings. Idempotent by the emptiness guard; a second boot does nothing. This service WRITES group files — it is the sanctioned exception to "machine never writes groups" because it materializes the user's OWN pre-existing appsettings declaration once; put that sentence in the class doc.

- [ ] Steps: failing tests on `Convert` (mixed spot+perp assets → two groups; Eager flag honored; ticks lazy via replenishable registry; `Enabled == false` feed omitted from the group; quote suffix longest-match: `XXXBUSD` → `XXX/BUSD` (BUSD wins over the absent USD), a symbol ending in bare `USD` → warning not crash; unmappable symbol → warning not crash), red, implement (+service), green + full suite, commit: `feat(groups): one-shot legacy appsettings import`.

---

### Task 7: Groups + validate endpoints

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/GroupEndpoints.cs`
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs` (`app.MapGroupEndpoints()`)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Groups/GroupEndpointsTests.cs` (direct handler calls, `internal static` per repo pattern)

Routes (all under `/api/v1`):
- `GET /groups` → `{ groups: [ { name, enabled, exchanges, symbol_count, feed_count, etag } ] }` (summaries from `IGroupStore.List`).
- `GET /groups/{name}` → full group JSON + `ETag` response header; 404 with `{error, name}`.
- `PUT /groups/{name}` → body = group JSON; `If-Match` header = expected ETag (absent → create). 200 `{etag}`; 409 `{error:"concurrency_conflict"}` from `ConcurrencyConflictException`; 422 `{error:"validation_failed", errors:[…]}` from `GroupValidationException`; 422 on name regex/mismatch.
- `DELETE /groups/{name}` → 204 / 404.
- `POST /groups/validate` → body = group JSON (NOT persisted). Runs `GroupValidator` + `GroupExpansion.Expand` over the set: **the submitted group (forced `Enabled = true` for the preview — a disabled draft would expand to 0 tuples and show nothing) + all STORED enabled groups EXCLUDING any stored group with the same name** (otherwise editing an existing group makes it conflict with its own saved version). Returns the preview: `{ errors: [...], expansion: { tuple_count, unsupported: [{exchange, canonical, reason}], conflicts: [...], per_exchange: [{exchange, symbols, feeds}], already_materialized: N } }` where `already_materialized` counts tuples whose `(exchange, Venue.Dir, FeedName, Interval)` has ANY index rows (cheap `IHistoryIndex.ListFeedKeys` per dir, cached per request in a dictionary). Cross-group conflicts must surface here BEFORE save (spec §3.1).

- [ ] Steps: failing handler tests (CRUD happy paths; 409 stale etag; 422 validation with error list; validate preview returns unsupported + conflict vs a second store group + counts; validating an EDIT of a stored group does not conflict with its own saved version (same-name exclusion); a disabled draft still previews its tuples), red, implement, green + full suite, commit: `feat(groups): CRUD + validate endpoints with CAS etags`.

---

### Task 8: Dry-run reconciler + desired-state endpoint

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Groups/ConvergenceEvaluator.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Groups/ConvergenceReport.cs` (records: `TupleStatus`, `OrphanEntry`, `ConvergenceReport` — result shapes, one file)
- Create: `src/AlgoTradeForge.HistoryLoader.WebApi/Groups/DesiredStateService.cs` (hosted: subscribes `GroupsChanged` with 500ms debounce, recomputes, holds latest report)
- Create: `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/DesiredStateEndpoints.cs` (`GET /api/v1/desired-state?exchange=`)
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Groups/ConvergenceEvaluatorTests.cs`

**Interfaces:**

```csharp
public sealed record TupleStatus(DesiredTuple Tuple, string Status, int MonthsExpected, int MonthsCovered);
// Status: unsupported | on-demand | missing | partial | materialized
public sealed record OrphanEntry(string Exchange, string Dir, string FeedName, string Interval);
public sealed record ConvergenceReport(
    DateTimeOffset ComputedAt,
    IReadOnlyList<TupleStatus> Tuples,
    IReadOnlyList<OrphanEntry> Orphaned,      // indexed feeds no enabled group references (spec §3.4: NEVER auto-deleted)
    IReadOnlyList<GroupConflict> Conflicts);

public sealed class ConvergenceEvaluator(IHistoryIndex index, SymbologyRegistry registry)
{
    /// <summary>Dry-run diff (phase-2 subset). Coarse month counting: covered = month_partitions
    /// row exists / CompleteMonths contains month; expected = months from HistoryStart..now UTC
    /// inclusive. Status rules IN ORDER: unsupported → Venue null; on-demand → (Collect ==
    /// "on-demand" OR Tuple.IsDerived — phase 2 cannot materialize derived regardless of their
    /// materialize value) AND 0 covered — an expected state, NOT a discrepancy; missing →
    /// 0 covered AND expected > 0 (future historyStart ⇒ expected 0 ⇒ vacuously converged, not
    /// missing); materialized → covered ≥ expected; else partial. Orphans:
    /// every index ListAssets×ListFeedKeys key not claimed by any tuple (match on (exchange
    /// OrdinalIgnoreCase — tuples are lowercase, index rows may not be, dir, feedName, interval));
    /// the equity root will be almost entirely orphaned in phase 2 — expected and correct
    /// (nothing declares it yet).</summary>
    public Task<ConvergenceReport> Evaluate(IReadOnlyList<CollectionGroup> groups, CancellationToken ct = default);
}
```

`DesiredStateService`: the FIRST compute runs unconditionally at service start, BEFORE subscribing to `GroupsChanged` (Program.cs already orders it after `LegacyImportService`, so imported groups are on disk; subscribing first would re-create the phase-1 startup-race shape); thereafter recompute on debounced (500ms) `GroupsChanged`. `GET /desired-state` serves the held report (plus `?exchange=` filter applied at serialization); never blocks on a compute — returns last report with its `computed_at`. Derived tuples (interval "") get status by their own feed rows; ticks/funding use `CompleteMonths` from `GetFeedStatuses`.

- [ ] Steps: failing evaluator tests over real SqliteHistoryIndex temp fixture (materialized: 3/3 months; partial 1/3; missing (eager, 0 covered); on-demand (collect: on-demand, 0 covered → `on-demand`, NOT missing; same tuple WITH rows → partial/materialized by the normal rules; derived tuple with materialize: eager, 0 covered → still `on-demand` via IsDerived); eager tuple with FUTURE historyStart → expected 0 → `materialized`, not missing; unsupported FUT; orphan detection incl. equity-shaped rows AND mixed-case index exchange matched by a lowercase tuple; conflicts passed through), red, implement + hosted service + endpoint, green + full suite, commit: `feat(groups): dry-run convergence evaluator + /desired-state`.

---

### Task 9: Main-WebApi proxy routes

**Files:**
- Modify: `src/AlgoTradeForge.WebApi/Endpoints/DataEndpoints.cs`
- Test: extend `tests/AlgoTradeForge.WebApi.Tests/Data/DataProxyTests.cs`

Add pass-through routes on the existing HistoryLoader proxy client (same pattern as `/api/data/loads` — status + body + relevant headers forwarded, JsonException → 400):
- `GET/PUT/DELETE /api/data/groups/{name}` (forward `If-Match` request header and `ETag` response header — the ONLY header-forwarding subtlety in this task; the existing helper may drop headers, extend minimally),
- `GET /api/data/groups`, `POST /api/data/groups/validate`, `GET /api/data/desired-state`.
No caching on any of these (they are config/live-status, not catalog).

- [ ] Steps: failing proxy tests (etag round-trip both directions; 409/422 pass-through; validate POST body forwarded), red, implement, green: `dotnet test tests/AlgoTradeForge.WebApi.Tests/`, commit: `feat(groups): main-api proxy for groups + desired-state`.

---

### Task 10: FE — wire types + dataApi methods

**Files:**
- Modify: `frontend/types/data-tab.ts` (add `CollectionGroupSummary`, `CollectionGroupDoc`, `ValidatePreview`, `DesiredStateReport`, `TupleStatus` — mirror the wire shapes from Tasks 7-8, snake_case fields)
- Modify: `frontend/lib/services/data-api.ts` (add `getGroups`, `getGroup(name)` → `{group, etag}` (etag from response header), `putGroup(name, body, etag?)` (If-Match; surface 409/422 as typed `DataApiError` codes), `deleteGroup`, `validateGroup(body)`, `getDesiredState(exchange?)`)
- Test: `frontend/lib/services/__tests__/data-api-groups.test.ts` (fetch-mocked: etag header extraction, If-Match sent, 409 → DataApiError code "concurrency_conflict")

- [ ] Steps: failing tests, implement, `npx tsc --noEmit` + `npm test` green, commit: `feat(fe): groups + desired-state api client`.

---

### Task 11: FE — Groups zone UI

**Files:**
- Create: `frontend/components/features/data/groups/groups-panel.tsx` (zone root: TanStack Query `["data","groups"]` + `["data","desired-state"]`; card grid)
- Create: `frontend/components/features/data/groups/group-card.tsx` (name, exchanges chips, symbol/feed counts, enabled badge, convergence summary — materialized/partial/missing counts filtered from desired-state for this group's name in `TupleStatus.tuple.groups`; `on-demand` tuples are NOT counted as missing — show them as a separate neutral (non-alarm) chip: lazy feeds and unmaterialized derived are expected states, not gaps)
- Create: `frontend/components/features/data/groups/group-editor.tsx` (CodeMirror 6 JSON editor — reuse the `EDITOR_EXTENSIONS` recipe from `frontend/components/features/dashboard/run-new-panel.tsx` lines ~35-44: `basicSetup + json() + linter(jsonParseLinter()) + oneDark`; Validate button → `validateGroup` → preview panel with expansion counts / unsupported list / conflicts; Save → `putGroup` with held etag; 409 → toast "group changed on server — reload"; 422 → error list rendered under the editor; Create mode with a template skeleton constant)
- Create: `frontend/components/features/data/groups/validate-preview.tsx`
- Modify: `frontend/components/features/data/data-tab-root.tsx` (zone switch: existing Explorer grid | new Groups — a simple two-tab toggle in the tab header, Explorer default)
- Test: `frontend/components/features/data/groups/__tests__/group-editor.test.tsx` (validate flow renders preview; save sends If-Match; 409 path shows reload toast), `groups-panel.test.tsx` (cards render from mocked queries incl. convergence counts)

Template skeleton for Create mode (constant in group-editor.tsx, matches spec §3.1 exactly):

```json
{
  "name": "my-group",
  "enabled": true,
  "exchanges": ["binance"],
  "assets": { "symbols": ["BTC/USDT-PERP"], "historyStart": "2024-01" },
  "feeds": {
    "candles": { "collect": "eager", "intervals": ["1m", "1h"] },
    "funding-rate": { "collect": "eager" }
  },
  "derived": {}
}
```

- [ ] Steps: failing component tests, implement, `npx tsc --noEmit` + `npm run lint` + `npm test` green, commit: `feat(fe): groups zone — cards, CodeMirror editor, validate preview`.

---

### Task 12: Whole-branch regression + live smoke

- [ ] Sequential full regression (ONE dotnet process): build slnx → HistoryLoader.Tests → Domain.Tests → Application.Tests → Infrastructure.Tests → WebApi.Tests → private Full.slnx build → FE `npx tsc --noEmit` + `npm run lint` + `npm test`. Every count reported; branch-caused failures fixed, never deferred.
- [ ] Live smoke (VS Code launch settings, `HistoryTest` root, ports 5000/5051/3000 — NOT the :5210 service): boot HistoryLoader fresh → legacy import creates `legacy-binance-*` groups (log check) → FE Groups zone lists them → edit one in the editor → Validate shows expansion preview → Save round-trips ETag → `/desired-state` shows materialized/partial statuses against the HistoryTest index → concurrent stale save shows the 409 toast. Use the `validate` skill (Playwright) if convenient.
- [ ] Update `.superpowers/sdd/progress.md` ledger; hand to final whole-branch review per the executing skill's flow.

---

## Self-review notes (already applied)

- Spec coverage: §3.1 groups+semantics (Tasks 3,5,7), §3.2 symbology (1,2), §3.4 dry-run statuses + orphans (8), §3.6 zone 1 (11), §3.7 API surface (7,8,9); import (§3.1 last bullet) = Task 6. `discovered_first_month` write-path is EXCLUDED (phase 3 — it needs `ISettingsWriter` retirement, which is coupled to reconciler-driven collection).
- Type consistency: `VenueInstrument` produced in Task 2 and consumed by Tasks 5/8; `DesiredTuple.Groups` consumed by FE card filtering (Task 11); `GroupConflict` flows 5→7→8→FE preview.
- The orphan flood from the equity root in `/desired-state` is expected phase-2 behavior (nothing declares equity yet) — the FE shows orphan COUNT, not the 12k list; the endpoint caps `orphaned` in the response at 500 entries + `orphaned_total` (add this cap in Task 8's endpoint serialization — it IS in scope).
