# LiveHost Host Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract the live-trading code into a dedicated `LiveHost` vertical-slice host, split the fused Binance connector along the ingest/execution seam, bound every channel, land a real `BinanceVenueConnector`, and wire the relay so live `aggTrade` ticks archive as `.atft` — closing the capture→archive→canonicalize round-trip.

**Architecture:** Three new projects (`LiveHost.Application`, `LiveHost.Infrastructure`, `LiveHost.WebApi`) mirror the HistoryLoader quartet; `Domain` stays shared. The ingest plane (`BinanceVenueConnector : IVenueConnector`) feeds the Plan-1 relay producer; the execution plane (slimmed `BinanceLiveConnector`) keeps orders/fills/reconciliation on bounded channels. Bar→strategy delivery is intentionally severed and deferred to Plan 4.

**Tech Stack:** C# 14 / .NET 10, ASP.NET Core minimal APIs, `System.Threading.Channels`, `Microsoft.Extensions.Hosting` (BackgroundService), xUnit v3 + NSubstitute, `Microsoft.Extensions.Time.Testing.FakeTimeProvider`, `AlgoTradeForge.Live.Relay`, `IFileStorage`.

## Global Constraints

- **Target:** C# 14 / .NET 10. `Nullable` enable, `ImplicitUsings` enable.
- **One `dotnet` process at a time** — never run build/test/run in parallel; wait for each to finish.
- **Shell:** `powershell.exe` (not `pwsh`). Commit messages via bash heredoc + `git commit -F -` (never PowerShell `Out-File` — injects a UTF-8 BOM).
- **Int64 money convention:** Domain casts use `MoneyConvert.ToLong()`; Application/Infrastructure boundary uses `ScaleContext` (`scale.FromMarketPrice`, `scale.ToMarketPrice`, `scale.AmountToTicks`). Raw `(long)` only for non-monetary values.
- **Every channel is bounded** (§A invariant 1). No new `Channel.CreateUnbounded`.
- **Async I/O convention:** no `Async` suffix on new/changed async methods; `CancellationToken ct = default` on async APIs; no `.Result`/`.Wait()`/`.GetAwaiter().GetResult()` at production call sites.
- **`using` over try/finally** for single-release cleanup.
- **Comment convention:** prefer no comments; allowed only for non-obvious algorithm/pitfall/TODO. No signature restatement.
- **One type per file**, named after the type.
- **xUnit analyzers:** `Assert.Single()` / `Assert.Empty()` (not `Assert.Equal(1, …)` / `Assert.Equal(0, …)`). Use `TestContext.Current.CancellationToken` in tests.
- **No auto-staging** is the standing rule, **overridden for this branch** (`feat/livehost-host-extraction`) — owner authorized per-task commits, as with Plans 1 & 2.
- **No deferred test failures:** when the seam split removes a feature, remove its test alongside the code (feature+test deleted together); do NOT leave a failing test annotated "pre-existing". Tests covering surviving behavior are migrated, not deleted.
- **Venue id:** `"binance"` (lowercase) — matches the `live-md/{venue}/…` relay key prefix and HistoryLoader `AssetDirectoryName` expectations.

---

## File Structure

**New projects:**
- `src/AlgoTradeForge.LiveHost.Application/` — moved `Application/Live/*` (CQRS handlers, session store, abstractions, DTOs). Namespace `AlgoTradeForge.LiveHost.Application`.
- `src/AlgoTradeForge.LiveHost.Infrastructure/` — moved `Infrastructure/Live/*` + new `BinanceVenueConnector`. Namespace `AlgoTradeForge.LiveHost.Infrastructure`. Refs: `LiveHost.Application`, `Domain`, `Live.Relay`, `Storage`.
- `src/AlgoTradeForge.LiveHost.WebApi/` — new host: `Program.cs`, `Endpoints/LiveEndpoints.cs`, `LiveHostServiceCollectionExtensions.cs`, `RelayPumpHostedService.cs`, `RelayPumpOptions.cs`, `appsettings*.json`. Refs all three above.
- `tests/AlgoTradeForge.LiveHost.Application.Tests/`, `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/`, `tests/AlgoTradeForge.LiveHost.WebApi.Tests/`.

**Modified (Plan-1 relay):**
- `src/AlgoTradeForge.Live.Relay/RelayIngest.cs` — scales from connector, not hardcoded.
- `src/AlgoTradeForge.Live.Relay/IVenueConnector.cs` — add per-instrument scale lookup.

**De-referenced:**
- `src/AlgoTradeForge.WebApi/Program.cs` — drop live usings, `Configure<BinanceLiveOptions>`, `MapLiveEndpoints()`.
- `src/AlgoTradeForge.Application/DependencyInjection.cs` — drop the 4 live registrations (lines 89-93).
- Delete `src/AlgoTradeForge.WebApi/Endpoints/LiveEndpoints.cs`, `src/AlgoTradeForge.Application/Live/`, `src/AlgoTradeForge.Infrastructure/Live/` after moves.

**Task → spec mapping:** T1 §A · T2-T4 §A (extraction) · T5 §B execution · T6 §B ingest · T7 §B scale-from-config · T8 §C bounded channels · T9 §D archival wiring · T10 §F round-trip · T11 §F/§G allocation+spec.

---

### Task 1: Scaffold the three LiveHost projects + test projects

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Application/AlgoTradeForge.LiveHost.Application.csproj`
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/AlgoTradeForge.LiveHost.Infrastructure.csproj`
- Create: `src/AlgoTradeForge.LiveHost.WebApi/AlgoTradeForge.LiveHost.WebApi.csproj`
- Create: `src/AlgoTradeForge.LiveHost.WebApi/Program.cs` (minimal placeholder)
- Create: `tests/AlgoTradeForge.LiveHost.Application.Tests/…csproj`, `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/…csproj`, `tests/AlgoTradeForge.LiveHost.WebApi.Tests/…csproj`
- Modify: `AlgoTradeForge.slnx`

**Interfaces:**
- Produces: five buildable empty projects + their solution entries. No types yet.

- [ ] **Step 1: Create the three src csproj files**

`LiveHost.Application.csproj` (classlib, mirrors `Application.csproj` package set — copy its `<PackageReference>`s and add the project refs it needs: `Domain`, `Application` for shared abstractions like `ICommandHandler`):
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\AlgoTradeForge.Domain\AlgoTradeForge.Domain.csproj" />
    <ProjectReference Include="..\AlgoTradeForge.Application\AlgoTradeForge.Application.csproj" />
  </ItemGroup>
</Project>
```
`LiveHost.Infrastructure.csproj` — same shell, refs:
```xml
  <ItemGroup>
    <ProjectReference Include="..\AlgoTradeForge.LiveHost.Application\AlgoTradeForge.LiveHost.Application.csproj" />
    <ProjectReference Include="..\AlgoTradeForge.Domain\AlgoTradeForge.Domain.csproj" />
    <ProjectReference Include="..\AlgoTradeForge.Application\AlgoTradeForge.Application.csproj" />
    <ProjectReference Include="..\AlgoTradeForge.Live.Relay\AlgoTradeForge.Live.Relay.csproj" />
    <ProjectReference Include="..\AlgoTradeForge.Storage\AlgoTradeForge.Storage.csproj" />
  </ItemGroup>
```
Copy any `<PackageReference>`s the current `Infrastructure.csproj` uses for the live code (e.g. `System.Net.WebSockets.Client` if present — check `Infrastructure.csproj` and copy only what `Live/` needs; defer pruning).

`LiveHost.WebApi.csproj` — `Sdk="Microsoft.NET.Sdk.Web"`, copy the package set from `HistoryLoader.WebApi.csproj` (Serilog, Hosting.Systemd/WindowsServices), refs:
```xml
  <ItemGroup>
    <ProjectReference Include="..\AlgoTradeForge.LiveHost.Application\AlgoTradeForge.LiveHost.Application.csproj" />
    <ProjectReference Include="..\AlgoTradeForge.LiveHost.Infrastructure\AlgoTradeForge.LiveHost.Infrastructure.csproj" />
    <ProjectReference Include="..\AlgoTradeForge.Live.Relay\AlgoTradeForge.Live.Relay.csproj" />
    <ProjectReference Include="..\AlgoTradeForge.Storage\AlgoTradeForge.Storage.csproj" />
  </ItemGroup>
```

- [ ] **Step 2: Create the placeholder host Program.cs**

```csharp
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/health", () => Results.Ok("livehost"));
app.Run();
```

- [ ] **Step 3: Create the three test csproj files**

Mirror `tests/AlgoTradeForge.Infrastructure.Tests/…csproj` (xUnit v3 + NSubstitute + `Microsoft.Extensions.Time.Testing` package set — copy verbatim). Each refs its system-under-test project:
- `LiveHost.Application.Tests` → `LiveHost.Application`
- `LiveHost.Infrastructure.Tests` → `LiveHost.Infrastructure`
- `LiveHost.WebApi.Tests` → `LiveHost.WebApi`

Add a trivial `SmokeTest.cs` to each so the runner has a test:
```csharp
public class SmokeTest { [Fact] public void Builds() => Assert.True(true); }
```

- [ ] **Step 4: Register all six projects in `AlgoTradeForge.slnx`**

Add inside `<Solution>` (src projects as top-level `<Project>`, test projects under the `/tests/` folder — match the existing file's two placement styles):
```xml
  <Project Path="src\AlgoTradeForge.LiveHost.Application\AlgoTradeForge.LiveHost.Application.csproj" />
  <Project Path="src\AlgoTradeForge.LiveHost.Infrastructure\AlgoTradeForge.LiveHost.Infrastructure.csproj" />
  <Project Path="src\AlgoTradeForge.LiveHost.WebApi\AlgoTradeForge.LiveHost.WebApi.csproj" />
  <Project Path="tests\AlgoTradeForge.LiveHost.Application.Tests\AlgoTradeForge.LiveHost.Application.Tests.csproj" />
  <Project Path="tests\AlgoTradeForge.LiveHost.Infrastructure.Tests\AlgoTradeForge.LiveHost.Infrastructure.Tests.csproj" />
  <Project Path="tests\AlgoTradeForge.LiveHost.WebApi.Tests\AlgoTradeForge.LiveHost.WebApi.Tests.csproj" />
```

- [ ] **Step 5: Build to verify the solution graph**

Run: `dotnet build AlgoTradeForge.slnx`
Expected: Build succeeded, 0 errors. (New projects compile empty.)

- [ ] **Step 6: Commit**

```bash
git add src/AlgoTradeForge.LiveHost.* tests/AlgoTradeForge.LiveHost.* AlgoTradeForge.slnx
git commit -F - <<'EOF'
feat(livehost): scaffold LiveHost.Application/.Infrastructure/.WebApi + test projects

Empty vertical-slice projects mirroring the HistoryLoader quartet; registered
in the solution. No live code moved yet.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 2: Move `Application/Live` → `LiveHost.Application`

**Files:**
- Move: all `src/AlgoTradeForge.Application/Live/*.cs` → `src/AlgoTradeForge.LiveHost.Application/Live/`
- Move: `tests/AlgoTradeForge.Application.Tests/Live/*.cs` → `tests/AlgoTradeForge.LiveHost.Application.Tests/Live/`
- Modify: `src/AlgoTradeForge.WebApi/Endpoints/LiveEndpoints.cs` (usings), `src/AlgoTradeForge.Application/DependencyInjection.cs` (remove live block), `src/AlgoTradeForge.WebApi/AlgoTradeForge.WebApi.csproj` (+ref LiveHost.Application)

**Interfaces:**
- Produces: `AlgoTradeForge.LiveHost.Application.Live` namespace exporting `StartLiveSessionCommand(+Handler)`, `StopLiveSessionCommand(+Handler)`, `GetLiveSessionDataQuery(+Handler)`, `ILiveSessionStore`, `InMemoryLiveSessionStore`, `ILiveSessionDataProvider`, `IExchangeOrderClient`, `LiveSessionSnapshot`, `SessionDetails`, `LiveSessionSubmissionDto`, `LiveSessionDataDto`, etc. — same type names, new namespace.

- [ ] **Step 1: `git mv` the source files**

```bash
mkdir -p src/AlgoTradeForge.LiveHost.Application/Live
git mv src/AlgoTradeForge.Application/Live/*.cs src/AlgoTradeForge.LiveHost.Application/Live/
```

- [ ] **Step 2: Rename the namespace in every moved file**

In each moved file change `namespace AlgoTradeForge.Application.Live;` → `namespace AlgoTradeForge.LiveHost.Application.Live;` (Edit per file; do not use sed — see memory note on sed mangling).

- [ ] **Step 3: Fix consumers' usings**

`WebApi/Endpoints/LiveEndpoints.cs` and any other file with `using AlgoTradeForge.Application.Live;` → `using AlgoTradeForge.LiveHost.Application.Live;`. Add the project ref to `WebApi.csproj`:
```xml
    <ProjectReference Include="..\AlgoTradeForge.LiveHost.Application\AlgoTradeForge.LiveHost.Application.csproj" />
```

- [ ] **Step 4: Remove the live DI block from `Application/DependencyInjection.cs`**

Delete lines registering `ILiveSessionStore`/the three live handlers (current lines 89-93) and the `using AlgoTradeForge.Application.Live;`. These re-home in T4.

- [ ] **Step 5: Move + re-namespace the tests**

```bash
mkdir -p tests/AlgoTradeForge.LiveHost.Application.Tests/Live
git mv tests/AlgoTradeForge.Application.Tests/Live/*.cs tests/AlgoTradeForge.LiveHost.Application.Tests/Live/
```
Update each moved test's `using`/`namespace` to `AlgoTradeForge.LiveHost.Application.*`. Delete the `SmokeTest.cs` placeholder from this test project.

- [ ] **Step 6: Build + test**

Run: `dotnet build AlgoTradeForge.slnx`
Expected: 0 errors (WebApi now references LiveHost.Application; backtest Application no longer registers live handlers — confirm nothing else in Application referenced them).
Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/`
Expected: PASS (moved live handler tests green).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -F - <<'EOF'
refactor(livehost): move Application/Live into LiveHost.Application

git mv + namespace rename; WebApi references the new project; live handler
registrations removed from the shared Application DI (re-homed in the host).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 3: Move `Infrastructure/Live` → `LiveHost.Infrastructure`

**Files:**
- Move: all `src/AlgoTradeForge.Infrastructure/Live/**/*.cs` → `src/AlgoTradeForge.LiveHost.Infrastructure/Live/`
- Move: `tests/AlgoTradeForge.Infrastructure.Tests/Live/**/*.cs` → `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/`
- Modify: `src/AlgoTradeForge.WebApi/Program.cs` (usings/refs as needed), `WebApi.csproj` (+ref LiveHost.Infrastructure)

**Interfaces:**
- Produces: `AlgoTradeForge.LiveHost.Infrastructure.Live` + `.Live.Binance` namespaces exporting `BinanceLiveConnector`, `BinanceLiveAccountManager`, `BinanceApiClient`, `BinanceWebSocketManager`, `BinanceLiveOptions`, `BinanceLiveSessionDataProvider`, `LiveOrderContext`, `OrderGroupReconciler`, message/model types — same names, new namespace.

- [ ] **Step 1: `git mv` the source tree**

```bash
mkdir -p src/AlgoTradeForge.LiveHost.Infrastructure/Live
git mv src/AlgoTradeForge.Infrastructure/Live/* src/AlgoTradeForge.LiveHost.Infrastructure/Live/
```

- [ ] **Step 2: Re-namespace**

Per moved file: `AlgoTradeForge.Infrastructure.Live` → `AlgoTradeForge.LiveHost.Infrastructure.Live` (and `.Live.Binance` likewise). Update internal cross-usings. Note `LiveOrderContext` uses `AlgoTradeForge.Application.Live` (`IExchangeOrderClient`) → now `AlgoTradeForge.LiveHost.Application.Live`.

- [ ] **Step 3: Update WebApi wiring refs**

`WebApi/Program.cs` line 17 `using AlgoTradeForge.Infrastructure.Live.Binance;` → `using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;`. Add `WebApi.csproj` ref to `LiveHost.Infrastructure` (temporary — removed in T4 when live DI leaves WebApi). If `BinanceLiveAccountManager`/connector were registered in `Infrastructure/DependencyInjection.cs`, move those registrations notes for T4.

- [ ] **Step 4: Move + re-namespace tests**

```bash
mkdir -p tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live
git mv tests/AlgoTradeForge.Infrastructure.Tests/Live/* tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/
```
Update usings/namespaces. The `Testnet/` fixtures move too (kept; they're `[Trait]`-gated manual tests). Delete this project's `SmokeTest.cs`. If a moved test needs `InternalsVisibleTo`, add `<InternalsVisibleTo Include="AlgoTradeForge.LiveHost.Infrastructure.Tests" />` to `LiveHost.Infrastructure.csproj`.

- [ ] **Step 5: Build + test**

Run: `dotnet build AlgoTradeForge.slnx`
Expected: 0 errors.
Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/`
Expected: PASS (Testnet tests skipped without creds).

- [ ] **Step 6: Commit** (heredoc, `refactor(livehost): move Infrastructure/Live into LiveHost.Infrastructure`).

---

### Task 4: New host — move `LiveEndpoints` + live DI into `LiveHost.WebApi`, de-reference backtest WebApi

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.WebApi/Endpoints/LiveEndpoints.cs` (from `WebApi/Endpoints/LiveEndpoints.cs`)
- Create: `src/AlgoTradeForge.LiveHost.WebApi/LiveHostServiceCollectionExtensions.cs`
- Rewrite: `src/AlgoTradeForge.LiveHost.WebApi/Program.cs`
- Create: `src/AlgoTradeForge.LiveHost.WebApi/appsettings.json`, `appsettings.Development.json`
- Move: `tests/AlgoTradeForge.WebApi.Tests/Endpoints/Live*.cs` + `tests/AlgoTradeForge.WebApi.Tests/Live/**` → `tests/AlgoTradeForge.LiveHost.WebApi.Tests/`
- Modify: `src/AlgoTradeForge.WebApi/Program.cs` (remove live), delete `WebApi/Endpoints/LiveEndpoints.cs`, remove WebApi's temporary LiveHost refs from T2/T3
- Move: `WebApi/Contracts/*` live request/response records used only by LiveEndpoints → LiveHost.WebApi (check `LiveSessionSubmissionResponse`, `LiveSessionStatusResponse`, `LiveSessionDataResponse`, `CandleResponse` etc. — move the live-only ones; shared ones get referenced or duplicated minimally).

**Interfaces:**
- Consumes: `LiveHost.Application.Live.*` handlers, `LiveHost.Infrastructure.Live.*` connector/account manager, `Live.Relay` producer.
- Produces: `MapLiveEndpoints()` extension + `AddLiveHost(this IServiceCollection, IConfiguration)` DI extension on the host.

- [ ] **Step 1: Move `LiveEndpoints.cs` to the host**

`git mv src/AlgoTradeForge.WebApi/Endpoints/LiveEndpoints.cs src/AlgoTradeForge.LiveHost.WebApi/Endpoints/LiveEndpoints.cs`; namespace → `AlgoTradeForge.LiveHost.WebApi.Endpoints`; fix usings to `LiveHost.Application.Live`. Move the live-only `Contracts` records similarly.

- [ ] **Step 2: Write `LiveHostServiceCollectionExtensions.cs`**

Consolidate every live registration here (the 4 handlers from old `Application/DependencyInjection.cs` lines 89-93, the `Configure<BinanceLiveOptions>` from old `WebApi/Program.cs` lines 77-78, plus `ILiveAccountManager`/connector/`IExchangeOrderClient`/`ILiveSessionDataProvider` registrations gathered from T3):
```csharp
public static class LiveHostServiceCollectionExtensions
{
    public static IServiceCollection AddLiveHost(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<BinanceLiveOptions>(config.GetSection("BinanceLive"));
        services.AddSingleton<ILiveSessionStore, InMemoryLiveSessionStore>();
        services.AddScoped<ICommandHandler<StartLiveSessionCommand, LiveSessionSubmissionDto>, StartLiveSessionCommandHandler>();
        services.AddScoped<ICommandHandler<StopLiveSessionCommand, bool>, StopLiveSessionCommandHandler>();
        services.AddScoped<IQueryHandler<GetLiveSessionDataQuery, LiveSessionDataDto?>, GetLiveSessionDataQueryHandler>();
        // + ILiveAccountManager, IAssetRepository, IStrategyFactory, IOptimizationSpaceProvider as the
        //   StartLiveSessionCommandHandler ctor requires — register or reference the same impls the
        //   backtest host used (these live in Application/Infrastructure and are shared).
        return services;
    }
}
```
The handler depends on `IStrategyFactory`, `IAssetRepository`, `IOptimizationSpaceProvider` — register the existing shared implementations (reuse `Application`/`Infrastructure` DI extensions the backtest host calls, or call them from the host's `Program.cs`).

- [ ] **Step 3: Write the host `Program.cs`**

Model on `HistoryLoader.WebApi/Program.cs` (Serilog, config, Swagger optional). Call the shared Application/Infrastructure DI registrations the handlers need, then `builder.Services.AddLiveHost(builder.Configuration);` and `app.MapLiveEndpoints();`. (The relay pump hosted service is added in T9.)

- [ ] **Step 4: Create `appsettings.json`**

```json
{
  "Serilog": { "MinimumLevel": "Information" },
  "BinanceLive": { "Accounts": {} },
  "Venue": "binance"
}
```

- [ ] **Step 5: Strip live from the backtest host**

`WebApi/Program.cs`: remove `using AlgoTradeForge.LiveHost.Application.Live;`, `using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;`, the `Configure<BinanceLiveOptions>` block, and `app.MapLiveEndpoints();`. Remove the temporary `LiveHost.Application`/`LiveHost.Infrastructure` project refs from `WebApi.csproj` (the backtest host must not reference live code). Delete now-empty `src/AlgoTradeForge.Application/Live/` and `src/AlgoTradeForge.Infrastructure/Live/` directories.

- [ ] **Step 6: Move the WebApi live tests**

`git mv` the live endpoint/Testnet tests into `tests/AlgoTradeForge.LiveHost.WebApi.Tests/`; re-namespace; delete the project's `SmokeTest.cs`. Tests using `WebApplicationFactory<Program>` now target the LiveHost host's `Program` — update the factory generic + ensure the host exposes a public/`internal`-visible `Program` (add `public partial class Program;` if needed, with `InternalsVisibleTo` for the test project).

- [ ] **Step 7: Build + targeted test + verify de-reference**

Run: `dotnet build AlgoTradeForge.slnx`
Expected: 0 errors.
Run: `dotnet test tests/AlgoTradeForge.LiveHost.WebApi.Tests/`
Expected: PASS.
Verify the backtest host is clean: `grep -rn "Live" src/AlgoTradeForge.WebApi/ --include=*.cs` returns no live-trading references (only incidental matches like "Alive"/"Delivery" if any).

- [ ] **Step 8: Commit** (`refactor(livehost): new LiveHost.WebApi host; de-reference live from backtest WebApi`).

---

### Task 5: Slim `BinanceLiveConnector` to the execution plane

**Files:**
- Modify: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceLiveConnector.cs`
- Modify/Move: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/BinanceLiveConnectorE2ETests.cs`

**Interfaces:**
- Consumes: `LiveOrderContext`, `OrderGroupReconciler`, `BinanceApiClient`, `BinanceWebSocketManager` (execution-only WS: user-data stream).
- Produces: `BinanceLiveConnector` with no ingest surface — removed members: `OnKlineMessage`, `MapTimeFrameToInterval`, `GetRecentKlinesAsync`, kline subscription loop in `AddSessionAsync`, `LiveSessionEntry.AccumulatedBars`/`LastBarPerSub`/`BarsLock`. `GetSessionSnapshotAsync` returns empty `bars`/`lastBars`. `OnBarStart`/`OnBarComplete` strategy calls removed.

- [ ] **Step 1: Write the failing test — execution survives without ingest**

In `BinanceLiveConnectorE2ETests.cs`, keep/adjust a test asserting an order placed through a session reaches the `IExchangeOrderClient` and a fill flows back (using NSubstitute fakes for `BinanceApiClient`/WS). Add an assertion that `GetSessionSnapshotAsync` returns an **empty** candle list:
```csharp
var snap = await connector.GetSessionSnapshotAsync(sessionId, TestContext.Current.CancellationToken);
Assert.Empty(snap!.Candles);   // bar delivery deferred to Plan 4
```
Delete the test(s) that asserted `OnBarComplete`/`OnKlineMessage` drove the strategy — that behavior is removed with the code (returns in Plan 4). Do not leave them failing.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter FullyQualifiedName~BinanceLiveConnectorE2E`
Expected: FAIL (snapshot still returns klines / member shape mismatch).

- [ ] **Step 3: Remove the ingest surface**

In `BinanceLiveConnector.cs`: delete `OnKlineMessage`, `MapTimeFrameToInterval`, `GetRecentKlinesAsync`; remove the `foreach (var sub in config.Subscriptions) { … SubscribeKline … }` block in `AddSessionAsync`; remove `AccumulatedBars`/`LastBarPerSub`/`BarsLock` from `LiveSessionEntry` and all references; in `GetSessionSnapshotAsync` return empty `bars`/`lastBars`. Keep `EventQueue`/`ProcessingTask`, fills, reconciliation, account caching, `StopAsync`. The market-data `BinanceWebSocketManager.SubscribeKline` call site is gone — keep the user-data WS connection (execution reports).

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter FullyQualifiedName~BinanceLiveConnector`
Expected: PASS.

- [ ] **Step 5: Build full solution** — `dotnet build AlgoTradeForge.slnx` → 0 errors.

- [ ] **Step 6: Commit** (`refactor(livehost): slim BinanceLiveConnector to the execution plane (sever bar delivery → Plan 4)`).

---

### Task 6: New `BinanceVenueConnector : IVenueConnector` (ingest plane)

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceVenueConnector.cs`
- Create: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/BinanceVenueConnectorTests.cs`
- Reference: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceWebSocketManager.cs` (existing `SubscribeAggTrade` or add one), `BinanceMessages.cs` (aggTrade DTO)

**Interfaces:**
- Consumes: `IVenueConnector` (Live.Relay), `TradeEvent`, `TradeTick`, `AggressorSide` (Domain.History), `ScaleContext`.
- Produces: `BinanceVenueConnector` implementing `string Venue => "binance"`, `MarketDataSessionPolicy SessionPolicy => MarketDataSessionPolicy.Concurrent`, `IAsyncEnumerable<IMarketEvent> Stream(IReadOnlyList<string> instruments, CancellationToken ct)`. Also produces per-instrument scale via the T7 interface addition.

- [ ] **Step 1: Write the failing test — aggTrade DTO → canonical `TradeEvent`**

Test the pure normalization (no live socket): feed a parsed `BinanceAggTrade`-shaped record through the connector's internal `ToTradeEvent(instrument, dto)` mapper and assert the canonical fields:
```csharp
[Fact]
public void MapsAggTradeToCanonicalTradeEvent()
{
    var scale = new ScaleContext(/* BTCUSDT-like asset, tick 0.01 */ TestAssets.BtcUsdt);
    var dto = new BinanceAggTrade(EventTimeMs: 1_700_000_000_001, AggId: 42,
        Price: "50000.00", Quantity: "1", IsBuyerMaker: true);
    var ev = BinanceVenueConnector.ToTradeEvent("BTCUSDT", dto, scale);
    Assert.Equal("BTCUSDT", ev.Instrument);
    Assert.Equal(1_700_000_000_001, ev.Tick.TimestampMs);
    Assert.Equal(scale.FromMarketPrice(50000.00m), ev.Tick.Price);
    Assert.Equal(42, ev.Tick.Sequence);
    Assert.Equal(AggressorSide.Sell, ev.Tick.AggressorSide);  // buyer is maker ⇒ aggressor sells
}
```
(If `BinanceAggTrade` doesn't exist yet, add the DTO record in `BinanceMessages.cs` matching Binance `@aggTrade` JSON fields `E,a,p,q,m`.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter FullyQualifiedName~BinanceVenueConnector`
Expected: FAIL (type/method missing).

- [ ] **Step 3: Implement the connector**

```csharp
public sealed class BinanceVenueConnector(BinanceLiveOptions options, ILogger<BinanceVenueConnector> logger) : IVenueConnector
{
    public string Venue => "binance";
    public MarketDataSessionPolicy SessionPolicy => MarketDataSessionPolicy.Concurrent;

    internal static TradeEvent ToTradeEvent(string instrument, BinanceAggTrade dto, ScaleContext scale)
    {
        var side = dto.IsBuyerMaker ? AggressorSide.Sell : AggressorSide.Buy;
        var tick = new TradeTick(
            dto.EventTimeMs,
            scale.FromMarketPrice(decimal.Parse(dto.Price, CultureInfo.InvariantCulture)),
            scale.FromMarketPrice(decimal.Parse(dto.Quantity, CultureInfo.InvariantCulture)),
            dto.AggId,
            side);
        return new TradeEvent(instrument, tick);
    }

    public async IAsyncEnumerable<IMarketEvent> Stream(
        IReadOnlyList<string> instruments,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateBounded<IMarketEvent>(
            new BoundedChannelOptions(options.IngestChannelCapacity) { SingleReader = true });
        await using var ws = new BinanceWebSocketManager(options.MarketStreamUrl, options.ReconnectDelay, options.MaxReconnectAttempts, logger);
        ws.Start(CancellationTokenSource.CreateLinkedTokenSource(ct));
        foreach (var symbol in instruments)
        {
            var scale = ScaleFor(symbol);
            ws.SubscribeAggTrade(symbol, dto => channel.Writer.TryWrite(ToTradeEvent(symbol, dto, scale)));
        }
        await foreach (var ev in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return ev;
    }

    private ScaleContext ScaleFor(string symbol) => /* from options per-instrument scale (T7) */;
}
```
Add `SubscribeAggTrade(string symbol, Action<BinanceAggTrade> onMsg)` to `BinanceWebSocketManager` mirroring the existing `SubscribeKline` pattern (parse `@aggTrade` JSON). Add `IngestChannelCapacity` (default 4096) and per-instrument scale config to `BinanceLiveOptions`.

- [ ] **Step 4: Run tests** → PASS. (Live socket path is exercised manually / in T10's round-trip with a fake.)

- [ ] **Step 5: Build** → 0 errors.

- [ ] **Step 6: Commit** (`feat(livehost): BinanceVenueConnector ingest plane — aggTrade → canonical TradeEvent`).

---

### Task 7: Scale-from-config in `RelayIngest` (retire the hardcoded `(2,0)` debt)

**Files:**
- Modify: `src/AlgoTradeForge.Live.Relay/IVenueConnector.cs`, `src/AlgoTradeForge.Live.Relay/RelayIngest.cs`
- Modify: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceVenueConnector.cs` (implement the new member)
- Modify: `tests/AlgoTradeForge.Live.Relay.Tests/VenueIngestTests.cs`

**Interfaces:**
- Produces: `IVenueConnector.InstrumentScale(string instrument) → (sbyte PriceScaleExp, sbyte QtyScaleExp)`; `RelayIngest.Pump` registers each instrument with the connector-supplied scale instead of `(2, 0)`.

- [ ] **Step 1: Write the failing test**

Extend `VenueIngestTests` `FakeVenueConnector` to return a non-`(2,0)` scale (e.g. `(4, 3)`) and assert the segment header records it (read via `SegmentReader<TradeTick>` header / `CanonicalScale`). Assert the registered scale equals what the connector advertised, not the old literal.

- [ ] **Step 2: Run → FAIL** (member missing; still hardcoded).

- [ ] **Step 3: Implement**

Add to `IVenueConnector`:
```csharp
(sbyte PriceScaleExp, sbyte QtyScaleExp) InstrumentScale(string instrument);
```
Change `RelayIngest.Pump`:
```csharp
foreach (var i in instruments)
{
    var (p, q) = connector.InstrumentScale(i);
    ids[i] = writer.RegisterInstrument(i, priceScaleExp: p, qtyScaleExp: q);
}
```
Delete the placeholder comment. Implement `InstrumentScale` on `BinanceVenueConnector` (from `BinanceLiveOptions` per-instrument scale; sensible default). Update `FakeVenueConnector` in `VenueIngestTests`.

- [ ] **Step 4: Run** `dotnet test tests/AlgoTradeForge.Live.Relay.Tests/` → PASS.

- [ ] **Step 5: Build** → 0 errors.

- [ ] **Step 6: Commit** (`feat(relay): per-instrument scale from connector; retire hardcoded (2,0) debt`).

---

### Task 8: Bound the three execution channels (opus — concurrency-critical)

**Files:**
- Modify: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceLiveConnector.cs` (per-session `EventQueue`), `src/AlgoTradeForge.LiveHost.Infrastructure/Live/LiveOrderContext.cs` (`_orderChannel`, `_cancelChannel`)
- Create: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/BoundedChannelSafetyTests.cs`

**Interfaces:**
- Produces: all three channels `Channel.CreateBounded(capacity)`; reconciliation marshaling uses `await EventQueue.Writer.WriteAsync(action, ct)` (not `TryWrite`); `LiveOrderContext.Submit`/`Cancel` reject-on-full (mark `OrderStatus.Rejected`, log) since they are synchronous. Capacity from options (default 1024).

- [ ] **Step 1: Write the failing test — no deadlock when `EventQueue` is full during reconciliation**

```csharp
[Fact]
public async Task Reconciliation_DoesNotDeadlock_WhenEventQueueSaturated()
{
    // Arrange a connector session whose ProcessingTask is briefly paused so the bounded
    // EventQueue fills; trigger a reconciliation snapshot round-trip (TryWrite→await tcs
    // would hang on a full queue). With WriteAsync it must complete once the reader drains.
    // Assert the snapshot TaskCompletionSource completes within a timeout.
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var completed = await RunSaturatedReconciliationRoundTrip(cts.Token);
    Assert.True(completed, "reconciliation round-trip must complete on a bounded queue");
}
```
Also add: `Submit` returns a `Rejected` order (not a lost write) when the order channel is forced full.

- [ ] **Step 2: Run → FAIL** (current `TryWrite` path can drop/hang under a bounded queue).

- [ ] **Step 3: Implement the bounded conversion**

`BinanceLiveConnector` `EventQueue`:
```csharp
public Channel<Action> EventQueue { get; } = Channel.CreateBounded<Action>(
    new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
```
In `RunReconciliationLoopAsync`, replace each `entry.EventQueue.Writer.TryWrite(() => …)` that is followed by `await …Task` with `await entry.EventQueue.Writer.WriteAsync(() => …, ct)`. The fire-and-forget callback writes from `OnExecutionReport`/fill handlers stay `TryWrite` but **must not silently drop** — capacity is sized above realistic execution-report bursts; on `TryWrite == false`, log an error (this is an alarm, not normal flow).

`LiveOrderContext`:
```csharp
private readonly Channel<OrderRequest> _orderChannel = Channel.CreateBounded<OrderRequest>(
    new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
// _cancelChannel likewise
```
In `Submit`, after `_orderChannel.Writer.TryWrite(...)`, if it returns `false`: set `order.Status = OrderStatus.Rejected`, remove from `_pendingOrders`, log, return the id. Same reject-on-full for `Cancel`. Capacity flows from an options value (add `LiveChannelCapacity` to `BinanceLiveOptions`, default 1024) threaded into both ctors.

Preserve the `StopAsync` drain-before-cancel ordering in both classes (it already completes the writer then awaits the reader before CTS cancel — bounded channels keep this correct).

- [ ] **Step 4: Run** the bounded-safety tests + the existing connector/order-context tests:
Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/`
Expected: PASS (no deadlock, no drop, drain order intact).

- [ ] **Step 5: Build** → 0 errors. Verify no `CreateUnbounded` remains: `grep -rn "CreateUnbounded" src/` → only non-live matches (ideally none in live).

- [ ] **Step 6: Commit** (`fix(livehost): bound EventQueue + order/cancel channels (§A invariant 1); WriteAsync marshaling`).

---

### Task 9: `RelayPumpHostedService` — wire ingest → relay → upload (archival plane)

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.WebApi/RelayPumpHostedService.cs`, `RelayPumpOptions.cs`
- Modify: `src/AlgoTradeForge.LiveHost.WebApi/Program.cs` (register the hosted service + relay deps), `appsettings.json`
- Create: `tests/AlgoTradeForge.LiveHost.WebApi.Tests/RelayPumpHostedServiceTests.cs`

**Interfaces:**
- Consumes: `BinanceVenueConnector` (`IVenueConnector`), `RelayWriter`, `LocalSegmentSink`, `SegmentUploader`, `RelayIngest.Pump`, `IFileStorage`.
- Produces: a `BackgroundService` that, for the configured collect-instruments, runs `RelayIngest.Pump` into a `RelayWriter`, and periodically `SegmentUploader.SweepOnce`. `RelayPumpOptions { string LocalRoot; string KeyPrefix = "live-md"; string[] Instruments; TimeSpan HeartbeatInterval; TimeSpan UploadInterval; }`.

- [ ] **Step 1: Write the failing test — pump archives a fake connector's events**

Use a `FakeVenueConnector` (as in `VenueIngestTests`) yielding two `TradeEvent`s; run the hosted service's core pump method against a temp `LocalRoot`; assert `.atft` files exist under `{root}/{instrument}/trades/` and a `_session` stream exists. Use `FakeTimeProvider`.
```csharp
[Fact]
public async Task Pump_ArchivesTradesAndSession()
{
    var svc = new RelayPumpHostedService(new FakeVenueConnector(events), opts, new FakeFileStorage(), timeProvider, logger);
    await svc.RunPumpOnce(["BTCUSDT"], TestContext.Current.CancellationToken);
    Assert.True(File.Exists(/* a *.atft under root/BTCUSDT/trades */));
}
```

- [ ] **Step 2: Run → FAIL** (type missing).

- [ ] **Step 3: Implement the hosted service**

```csharp
public sealed class RelayPumpHostedService(
    IVenueConnector connector, IOptions<RelayPumpOptions> opts,
    IFileStorage storage, TimeProvider time, ILogger<RelayPumpHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var o = opts.Value;
        var sink = new LocalSegmentSink(o.LocalRoot);
        var uploader = new SegmentUploader(storage, o.LocalRoot, o.KeyPrefix + "/" + connector.Venue);
        await using var writer = new RelayWriter(connector.Venue, sink, new StreamPipelineOptions(), time, o.HeartbeatInterval);
        var uploadLoop = UploadLoop(uploader, o.UploadInterval, ct);
        try { await RelayIngest.Pump(connector, writer, o.Instruments, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        await uploadLoop.ConfigureAwait(false);
        await uploader.SweepOnce(CancellationToken.None).ConfigureAwait(false); // final flush
    }
    private async Task UploadLoop(SegmentUploader up, TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval, time);
        try { while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) await up.SweepOnce(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}
```
`RelayWriter.Start(ct)` is invoked inside `RelayIngest.Pump` (it already calls `writer.Start`). `SessionStart`/`SessionEnd` are emitted by `RelayWriter` start/dispose — confirmed in the producer. (No extra connect/disconnect boundary needed beyond writer lifecycle for Plan 3.) Register in `Program.cs`: `builder.Services.Configure<RelayPumpOptions>(config.GetSection("RelayPump")); builder.Services.AddSingleton<IVenueConnector, BinanceVenueConnector>(); builder.Services.AddHostedService<RelayPumpHostedService>();` and the `IFileStorage` impl the host uses.

- [ ] **Step 4: Run** `dotnet test tests/AlgoTradeForge.LiveHost.WebApi.Tests/` → PASS.

- [ ] **Step 5: Build** → 0 errors.

- [ ] **Step 6: Commit** (`feat(livehost): RelayPumpHostedService wires ingest → relay → IFileStorage`).

---

### Task 10: Round-trip acceptance test — `aggTrade` → `.atft` → canonical CSV (open/closed)

**Files:**
- Create: `tests/AlgoTradeForge.LiveHost.WebApi.Tests/LiveRoundTripTests.cs`
- Reference: `StreamCanonicalizer<TradeTick>`, `TradeProjection`, `ITickFeedWriter`/`DailyTickCsvWriter` (HistoryLoader.Infrastructure.Canonicalization), `SegmentKeyParser`, `CanonicalScale`.

**Interfaces:**
- Consumes: the full producer (`BinanceVenueConnector`/fake → `RelayIngest.Pump` → `.atft` on a temp `IFileStorage`) and the Plan-2 consumer (`StreamCanonicalizer<TradeTick>` + `TradeProjection`).
- Produces: a green end-to-end lossless assertion + an open/closed re-assertion.

- [ ] **Step 1: Write the acceptance test**

```csharp
[Fact]
public async Task LiveTicks_RoundTrip_To_CanonicalCsv_Lossless()
{
    // 1. Produce: fake aggTrade events → RelayIngest.Pump → .atft uploaded to a temp IFileStorage
    // 2. Consume: StreamCanonicalizer<TradeTick> + TradeProjection tails the uploaded segments
    // 3. Assert canonical rows equal the source: ts, price, qty, is_buyer_maker, agg_id (in order, no loss)
    var produced = new[] {
        new TradeEvent("BTCUSDT", new TradeTick(1_700_000_000_001, 5_000_000_000, 1, 1, AggressorSide.Sell)),
        new TradeEvent("BTCUSDT", new TradeTick(1_700_000_000_002, 5_000_100_000, 2, 2, AggressorSide.Buy)),
    };
    // … run pump, run canonicalizer, read DailyTickCsvWriter output …
    Assert.Equal("1700000000001,50000.00000,1,1,1", rows[0]);   // is_buyer_maker = Sell?1:0 = 1
    Assert.Equal("1700000000002,50001.00000,2,2,0", rows[1]);
}
```
(Use the actual canonical column order/format from `DailyTickCsvWriter` — read it to mirror exactly. Scale exps come from the segment header via `CanonicalScale.Unscale`, set by the connector's `InstrumentScale`.)

- [ ] **Step 2: Run → FAIL** (wiring incomplete / assertion mismatch).

- [ ] **Step 3: Make it pass** — fix any scale/format mismatches surfaced; no new production behavior should be needed if T6-T9 are correct. If the test reveals a real producer/consumer contract gap, fix the production code (not the assertion).

- [ ] **Step 4: Add the open/closed re-assertion**

Mirror Plan-2's proof: a test-only second event type (e.g. the existing `QuoteTick` stream, or a `DepthTick` test double) canonicalizes through `StreamCanonicalizer<T>` with **zero edits** to canonicalizer production code. Assert it produces its own stream output.

- [ ] **Step 5: Run** the full LiveHost test set:
Run: `dotnet test tests/AlgoTradeForge.LiveHost.WebApi.Tests/` then `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/` then `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/`
Expected: PASS.

- [ ] **Step 6: Build full solution** → 0 errors.

- [ ] **Step 7: Commit** (`test(livehost): end-to-end lossless round-trip + open/closed re-assertion`).

---

### Task 11: Allocation measurement + record the push/visitor trigger

**Files:**
- Modify: `docs/superpowers/specs/2026-06-21-livehost-host-extraction-design.md` (§G — fill measured numbers + concrete trigger)
- Reference: `benchmarks/AlgoTradeForge.Benchmarks/` (Plan-1 1000-instrument firehose), `run-benchmarks` skill

**Interfaces:**
- Produces: a recorded allocation figure for the ingest seam under the firehose + a concrete, numeric push/visitor upgrade trigger in §G. No production code change.

- [ ] **Step 1: Pre-flight** — confirm no other `dotnet` process is running (benchmark CPU contention ruins the signal; `save-baseline.ps1` warns on competing PIDs).

- [ ] **Step 2: Run the firehose benchmark** via the `run-benchmarks` skill (the existing 1000-instrument relay benchmark from Plan 1). Capture Mean + **Allocated**.

- [ ] **Step 3: Record in spec §G** — replace the `N MB/s` placeholder with the measured seam allocation and a concrete trigger, e.g. *"Pull-seam allocation measured at X MB/s for 1000 instruments at 4 updates/s; upgrade to `IVenueSink.OnTrade(in TradeTick)` when sustained seam allocation exceeds 50 MB/s or at IB onboarding (whichever first)."* Use the real measured X.

- [ ] **Step 4: Commit** (`docs(livehost): record measured ingest-seam allocation + push/visitor upgrade trigger`).

---

### Task 12: Whole-branch review + close-out

- [ ] **Step 1:** Run the full solution build + every test project sequentially (one `dotnet` at a time): `dotnet build AlgoTradeForge.slnx`, then each `dotnet test tests/AlgoTradeForge.*/`. All green.
- [ ] **Step 2:** Verify the backtest host has zero live references and no `CreateUnbounded` remains in live code (grep).
- [ ] **Step 3:** Request a whole-branch opus code review (superpowers:requesting-code-review). Address findings.
- [ ] **Step 4:** Confirm the round-trip acceptance test (Task 10) passes as the close-out gate.

---

## Self-Review

**Spec coverage:** §A project layout → T1-T4. §B execution slim → T5; ingest connector → T6; scale-from-config → T7. §C bounded channels → T8. §D archival wiring → T9. §F round-trip + open/closed → T10; allocation → T11; final review → T12. §G push/visitor decision recorded → T11. Out-of-scope items (IStrategyDispatch, IOrderRouter, collection.json, alt-bar lib, spill-to-disk, _session dedup) correctly absent. **No gaps.**

**Placeholder scan:** Task code blocks carry real signatures. Remaining `/* … */` markers are deliberate "mirror the existing X exactly" pointers (DI package sets, `ScaleFor`, canonical CSV format) where the implementer must read the named existing file rather than copy a guess — each names the exact file to read. The only literal placeholder (`N MB/s`) is the *subject* of T11.

**Type consistency:** `IVenueConnector.InstrumentScale` (T7) consumed by `RelayIngest.Pump` (T7) and implemented by `BinanceVenueConnector` (T6→T7). `RelayWriter.RegisterInstrument(string, sbyte, sbyte)` / `WriteTrade(int, TradeTick, ct)` / `Start(ct)` / `DisposeAsync` match the real producer. `SegmentUploader(IFileStorage, localRoot, keyPrefix)` / `SweepOnce(ct)` match. `TradeEvent(string Instrument, TradeTick Tick)`, `TradeTick(TimestampMs, Price, Quantity, Sequence, AggressorSide)`, `AggressorSide.{Buy,Sell}` match Domain. Consistent.
