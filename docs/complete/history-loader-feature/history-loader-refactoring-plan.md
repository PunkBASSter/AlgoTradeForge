# Plan: Decompose HistoryLoader into Clean Architecture Layers

## Context

The `AlgoTradeForge.HistoryLoader` project is currently monolithic — Binance API clients, CSV storage, orchestration logic, rate limiting, endpoints, and background services all live in one project. Adding a second exchange (Bybit, Kraken) would require modifying core orchestration code. Decomposing into Domain/Application/Infrastructure layers forces proper interface boundaries, making multi-exchange support a matter of adding new implementations rather than modifying existing code.

## Shared Project Decision: Not Needed

The user asked to create a shared project "if there are any shared types." After analysis: there are none that require sharing between the two Domain layers.

- `FeedMetadata`, `FeedDefinition`, `CandleConfig`, `AutoApplyDefinition` stay in `AlgoTradeForge.Domain.History` — they're only consumed by HL.Infrastructure (which references `AlgoTradeForge.Domain` anyway for `MoneyConvert`)
- `KlineRecord`, `FeedRecord` are HistoryLoader-specific — not needed by the main Domain
- A new `AutoApplySpec` record in HL.Application decouples `SymbolCollector` from the main Domain's `AutoApplyDefinition`

## Target Structure

```
src/
  AlgoTradeForge.HistoryLoader.Domain/           # Pure DTOs, state models, utilities
  AlgoTradeForge.HistoryLoader.Application/       # Interfaces, orchestration, config
  AlgoTradeForge.HistoryLoader.Infrastructure/    # Binance clients, CSV I/O, rate limiting
  AlgoTradeForge.HistoryLoader/                   # WebApi host (endpoints, BackgroundServices)
```

## Dependency Graph

```
AlgoTradeForge.HistoryLoader.Domain (no deps)
       ↑
AlgoTradeForge.HistoryLoader.Application (→ HL.Domain)
       ↑
AlgoTradeForge.HistoryLoader.Infrastructure (→ HL.Application, AlgoTradeForge.Domain)
       ↑
AlgoTradeForge.HistoryLoader (WebApi host) (→ HL.Infrastructure, HL.Application)
```

HL.Infrastructure → AlgoTradeForge.Domain is for `MoneyConvert.ToLong()` (CandleCsvWriter) and `FeedMetadata`/`AutoApplyDefinition` (FeedSchemaManager).

---

## Step 1: Create `AlgoTradeForge.HistoryLoader.Domain`

**New project**: `src/AlgoTradeForge.HistoryLoader.Domain/` — net10.0 class library, zero dependencies.

Move & rename these types (change namespace, make `public`):

| Source | Target | Notes |
|--------|--------|-------|
| `HistoryLoader/Binance/KlineRecord.cs` | `HL.Domain/KlineRecord.cs` | Exchange-agnostic OHLCV DTO |
| `HistoryLoader/Binance/FeedRecord.cs` | `HL.Domain/FeedRecord.cs` | Generic `(long, double[])` DTO |
| `HistoryLoader/State/FeedStatus.cs` | `HL.Domain/FeedStatus.cs` | `FeedStatus`, `CollectionHealth`, `DataGap` |
| `HistoryLoader/Binance/BinanceIntervalMap.cs` | `HL.Domain/IntervalParser.cs` | Rename class to `IntervalParser` (format is industry-standard, not Binance-specific) |

All types change namespace to `AlgoTradeForge.HistoryLoader.Domain` and become `public`.

## Step 2: Create `AlgoTradeForge.HistoryLoader.Application`

**New project**: `src/AlgoTradeForge.HistoryLoader.Application/` — references HL.Domain. Needs `Microsoft.Extensions.Logging.Abstractions` and `Microsoft.Extensions.Options` packages.

### 2a. Define interfaces in `Abstractions/`

**`IFuturesDataFetcher.cs`** — mirrors `BinanceFuturesClient`'s public surface:
- `FetchKlinesAsync(symbol, interval, fromMs, toMs, ct) → IAsyncEnumerable<KlineRecord>`
- `FetchMarkPriceKlinesAsync(...)` → same
- `FetchFundingRatesAsync(symbol, fromMs, toMs, ct) → IAsyncEnumerable<FeedRecord>`
- `FetchOpenInterestAsync`, `FetchGlobalLongShortRatioAsync`, `FetchTopAccountRatioAsync`, `FetchTakerVolumeAsync`, `FetchTopPositionRatioAsync`, `FetchLiquidationsAsync` — all `→ IAsyncEnumerable<FeedRecord>`

**`ISpotDataFetcher.cs`** — single method: `FetchKlinesAsync → IAsyncEnumerable<KlineRecord>`

**`ICandleWriter.cs`** — `Write(assetDir, interval, record, decimalDigits)`, `ResumeFrom(assetDir, interval) → long?`

**`IFeedWriter.cs`** — `Write(assetDir, feedName, interval, columns, record)`, `ResumeFrom(assetDir, feedName, interval) → long?`

**`ISchemaManager.cs`** — `EnsureSchema(assetDir, feedName, interval, columns, autoApply?)`, `EnsureCandleConfig(assetDir, decimalDigits, interval)`. Includes `AutoApplySpec` record to decouple from Domain's `AutoApplyDefinition`:
```csharp
public sealed record AutoApplySpec(string Type, string RateColumn, string? SignConvention = null);
```

**`IFeedStatusStore.cs`** — `Load(assetDir, feedName) → FeedStatus?`, `Save(assetDir, feedName, status)`

### 2b. Move config types

Move `HistoryLoaderOptions.cs` (contains `HistoryLoaderOptions`, `BinanceOptions`, `AssetCollectionConfig`, `FeedCollectionConfig`) → namespace `AlgoTradeForge.HistoryLoader.Application`.

### 2c. Move orchestration

Move `SymbolCollector.cs` → `HL.Application/Collection/`. Refactor constructor:
- `BinanceFuturesClient` → `IFuturesDataFetcher`
- `BinanceSpotClient?` → `ISpotDataFetcher?`
- `CandleCsvWriter` → `ICandleWriter`
- `FeedCsvWriter` → `IFeedWriter`
- `FeedSchemaManager` → `ISchemaManager`
- Add `IFeedStatusStore` parameter (replaces static `FeedStatusManager` calls)

Internal changes:
- `BinanceIntervalMap.ToTimeSpan()` → `IntervalParser.ToTimeSpan()`
- `new AutoApplyDefinition { Type = "FundingRate", RateColumn = "rate" }` → `new AutoApplySpec("FundingRate", "rate")`
- `schemaManager.EnsureSchema(assetDir, name, new FeedCollectionConfig{...}, columns, autoApply)` → `schemaManager.EnsureSchema(assetDir, name, interval, columns, autoApply)`
- `FeedStatusManager.Load/Save(...)` → `feedStatusStore.Load/Save(...)`

Move `BackfillOrchestrator.cs` → `HL.Application/Collection/`. Namespace update only.

Both become `public`.

## Step 3: Create `AlgoTradeForge.HistoryLoader.Infrastructure`

**New project**: `src/AlgoTradeForge.HistoryLoader.Infrastructure/` — references HL.Application + `AlgoTradeForge.Domain`.

### 3a. Move Binance clients → `Infrastructure/Binance/`

- `BinanceFuturesClient` (+ 7 partials): implement `IFuturesDataFetcher`. Namespace → `...Infrastructure.Binance`.
- `BinanceSpotClient`: implement `ISpotDataFetcher`.
- `BinanceIntervalMap` stays here as `BinanceIntervalMap` (copy of the Binance-specific string constants/parsing that doesn't fit in the generic `IntervalParser` — **only if** there are Binance-specific mappings beyond the generic format; otherwise just use `IntervalParser` from HL.Domain and don't keep a copy).

### 3b. Move storage → `Infrastructure/Storage/`

- `CandleCsvWriter`: implement `ICandleWriter`. Keep `using AlgoTradeForge.Domain` for `MoneyConvert`.
- `FeedCsvWriter`: implement `IFeedWriter`.
- `FeedSchemaManager`: implement `ISchemaManager`. Map `AutoApplySpec` → `AutoApplyDefinition` internally. Remove `FeedCollectionConfig` from `EnsureSchema` signature (replaced by explicit `string interval` param).

### 3c. Move state → `Infrastructure/State/`

- `FeedStatusManager`: implement `IFeedStatusStore`. Convert from `static` class to instance class (interface requires instance methods).

### 3d. Move rate limiting → `Infrastructure/RateLimiting/`

- `WeightedRateLimiter`, `SourceRateLimiter` — namespace change only.

### 3e. Create `DependencyInjection.cs`

Extension method `AddHistoryLoaderInfrastructure(this IServiceCollection)` that registers:
- `WeightedRateLimiter` (singleton, configured from `BinanceOptions`)
- `IFuturesDataFetcher` → `BinanceFuturesClient` (with HttpClient + SourceRateLimiter)
- `ISpotDataFetcher` → `BinanceSpotClient` (with HttpClient + SourceRateLimiter)
- `ICandleWriter` → `CandleCsvWriter`
- `IFeedWriter` → `FeedCsvWriter`
- `ISchemaManager` → `FeedSchemaManager`
- `IFeedStatusStore` → `FeedStatusManager`

## Step 4: Slim Down WebApi Host

### 4a. Update `AlgoTradeForge.HistoryLoader.csproj`

- Remove reference to `AlgoTradeForge.Application`
- Add references to `HL.Infrastructure` + `HL.Application`
- Keep Serilog packages

### 4b. Delete moved files

Remove from the host project: `Binance/`, `Storage/`, `State/`, `RateLimiting/`, `HistoryLoaderOptions.cs`, `Collection/SymbolCollector.cs`, `Collection/BackfillOrchestrator.cs`.

**Remaining in host**:
```
Collection/  (6 BackgroundServices)
Endpoints/   (BackfillEndpoints, StatusEndpoints, Models)
Program.cs
appsettings.json
```

### 4c. Update `Program.cs`

```csharp
builder.Services.Configure<HistoryLoaderOptions>(...);
builder.Services.AddHistoryLoaderInfrastructure();
builder.Services.AddSingleton<SymbolCollector>();
builder.Services.AddSingleton<BackfillOrchestrator>();
// 6x AddHostedService (unchanged)
```

### 4d. Update endpoints

`StatusEndpoints.cs`: inject `IFeedStatusStore` as method parameter (replaces static `FeedStatusManager.Load()` calls). Also inject `BackfillOrchestrator` for `ResolveAssetDir`.

`Models.cs`: update `SymbolDetailResponse` to use `AlgoTradeForge.HistoryLoader.Domain.FeedStatus`.

### 4e. Update BackgroundServices

Each of the 6 services: update `using` statements for `HistoryLoaderOptions` (→ `...Application`), `SymbolCollector`/`BackfillOrchestrator` (→ `...Application.Collection`). No logic changes.

## Step 5: Move & Update Test Project

### 5a. Move test project to `src/`

Move `tests/AlgoTradeForge.HistoryLoader.Tests/` → `src/AlgoTradeForge.HistoryLoader.Tests/` so all HistoryLoader projects are co-located:

```
src/
  AlgoTradeForge.HistoryLoader/
  AlgoTradeForge.HistoryLoader.Domain/
  AlgoTradeForge.HistoryLoader.Application/
  AlgoTradeForge.HistoryLoader.Infrastructure/
  AlgoTradeForge.HistoryLoader.Tests/          ← moved from tests/
```

### 5b. Update `.csproj`

Project references change from `..\..\src\` to `..\` since the test project is now a sibling in `src/`:
```xml
<ProjectReference Include="..\AlgoTradeForge.HistoryLoader\AlgoTradeForge.HistoryLoader.csproj" />
<ProjectReference Include="..\AlgoTradeForge.HistoryLoader.Domain\AlgoTradeForge.HistoryLoader.Domain.csproj" />
<ProjectReference Include="..\AlgoTradeForge.HistoryLoader.Application\AlgoTradeForge.HistoryLoader.Application.csproj" />
<ProjectReference Include="..\AlgoTradeForge.HistoryLoader.Infrastructure\AlgoTradeForge.HistoryLoader.Infrastructure.csproj" />
```

### 5c. Update `using` statements across test files

| Test folder | Key namespace changes |
|-------------|----------------------|
| `Binance/*Tests.cs` | `...Binance` → `...Infrastructure.Binance` + `...Domain` |
| `Binance/BinanceIntervalMapTests.cs` | Rename to `IntervalParserTests.cs`, update refs |
| `Collection/GapDetectionTests.cs` | `...Binance` → `...Domain`, `BinanceIntervalMap` → `IntervalParser` |
| `Storage/*Tests.cs` | `...Storage` → `...Infrastructure.Storage`, `...Binance` → `...Domain` |
| `State/*Tests.cs` | `...State` → `...Infrastructure.State` + `...Domain` |
| `RateLimiting/*Tests.cs` | `...RateLimiting` → `...Infrastructure.RateLimiting` |

## Step 6: Update Solution File

Update `AlgoTradeForge.slnx` — add 3 new projects and update the test project path:
```xml
<Project Path="src\AlgoTradeForge.HistoryLoader.Domain\AlgoTradeForge.HistoryLoader.Domain.csproj" />
<Project Path="src\AlgoTradeForge.HistoryLoader.Application\AlgoTradeForge.HistoryLoader.Application.csproj" />
<Project Path="src\AlgoTradeForge.HistoryLoader.Infrastructure\AlgoTradeForge.HistoryLoader.Infrastructure.csproj" />
<!-- Update existing entry: -->
<!-- OLD: tests\AlgoTradeForge.HistoryLoader.Tests\... -->
<!-- NEW: src\AlgoTradeForge.HistoryLoader.Tests\...   -->
```

Also remove the test project from any `<Folder Name="/tests/">` grouping if it was in one (it wasn't — it was a top-level entry).

---

## Verification

1. `dotnet build AlgoTradeForge.slnx` — all projects compile
2. `dotnet test src/AlgoTradeForge.HistoryLoader.Tests/` — all existing tests pass (new path)
3. `dotnet test tests/AlgoTradeForge.Domain.Tests/` — no regressions in main domain
4. `dotnet test tests/AlgoTradeForge.Application.Tests/` — no regressions
5. Verify dependency direction: HL.Domain has zero `<ProjectReference>`, HL.Application references only HL.Domain, HL.Infrastructure references HL.Application + main Domain
