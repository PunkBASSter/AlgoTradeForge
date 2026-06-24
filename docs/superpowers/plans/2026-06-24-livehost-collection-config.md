# LiveHost collection.json (Plan 6a) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give LiveHost a CAS-protected `collection.json` (a list of `DataFeedSubscription`) that drives the relay capture set and gates live execution to collected feeds.

**Architecture:** `collection.json` is persisted via the existing `IFileStorage` CAS primitive and exposed through `GET/PUT /api/v1/config`. It reuses the Domain `DataFeedSubscription` vocabulary, so the relay pump derives its instrument list from the collected `Tick` feeds and session-start validates each strategy subscription is satisfied by the collected set (alt-bars via their root feed). No HistoryLoader changes, no new shared lib, no Domain changes.

**Tech Stack:** C# 14 / .NET 10, ASP.NET Core minimal APIs, `System.Text.Json` (existing `DataFeedSubscription` polymorphic converter), xunit.v3, NSubstitute.

**Spec:** `docs/superpowers/specs/2026-06-24-livehost-collection-config-design.md`

## Global Constraints

- **One `dotnet` process at a time** — build/test strictly sequential, never parallel. Use `powershell.exe`, not `pwsh`.
- **No implementer commits.** Per the repo SDD convention, implementer `git add`/`git commit` is hook-DENIED. Each task ends at **green tests**; the CONTROLLER stages + commits after the two-verdict review. Task steps therefore end with a "Controller checkpoint", not a git command.
- **Conventions:** no `Async` suffix on new async methods; `CancellationToken ct = default` on every async I/O method; `using`-over-try/finally; one type per file (single-line record may sit beside its interface); xUnit analyzers (`Assert.Single`/`Assert.Empty`, not `Assert.Equal(1/0, …)`).
- **No Domain changes** — `DataFeedSubscription` is reused as-is; `collection.json` includes `role` explicitly.
- **LiveHost must not depend on HistoryLoader.** The `ICollectionConfigStore` interface (LiveHost.Application) references only Domain types; the impl (LiveHost.Infrastructure) uses `IFileStorage` (namespace `AlgoTradeForge.Storage`).
- **JSON:** camelCase (`JsonSerializerDefaults.Web`), reusing the `DataFeedSubscription` `[JsonPolymorphic]` `"kind"` discriminator.

---

### Task 1: Collection config types + CAS store

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Application/Collection/CollectionConfig.cs`
- Create: `src/AlgoTradeForge.LiveHost.Application/Collection/ICollectionConfigStore.cs` (holds `ICollectionConfigStore` + the single-line `StoredCollectionConfig` record — file-org exception)
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Collection/CollectionConfigStore.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Collection/CollectionConfigStoreTests.cs`

**Interfaces:**
- Consumes: `IFileStorage.ReadWithEtag(string key, CancellationToken)` → `StoredObject?(Content, ETag)`; `IFileStorage.WriteIfMatch(string key, string content, string? expectedETag, CancellationToken)` → `string` (throws `ConcurrencyConflictException`). All in namespace `AlgoTradeForge.Storage`. `DataFeedSubscription` in `AlgoTradeForge.Domain.Strategy.Subscriptions`.
- Produces:
  - `record CollectionConfig(IReadOnlyList<DataFeedSubscription> Feeds)` — `AlgoTradeForge.LiveHost.Application.Collection`
  - `record StoredCollectionConfig(CollectionConfig Config, string? ETag)` — same namespace
  - `interface ICollectionConfigStore { Task<StoredCollectionConfig> Load(CancellationToken ct = default); Task<string> Save(CollectionConfig config, string? expectedETag, CancellationToken ct = default); }`
  - `class CollectionConfigStore(IFileStorage storage) : ICollectionConfigStore` — `AlgoTradeForge.LiveHost.Infrastructure.Collection`

- [ ] **Step 1: Write the failing test**

Create `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Collection/CollectionConfigStoreTests.cs`:

```csharp
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Application.Collection;
using AlgoTradeForge.LiveHost.Infrastructure.Collection;
using AlgoTradeForge.Storage;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Collection;

public class CollectionConfigStoreTests
{
    private const string Key = "collection.json";

    [Fact]
    public async Task Load_returns_empty_config_and_null_etag_when_file_absent()
    {
        var storage = Substitute.For<IFileStorage>();
        storage.ReadWithEtag(Key, Arg.Any<CancellationToken>()).Returns((StoredObject?)null);
        var store = new CollectionConfigStore(storage);

        var result = await store.Load();

        Assert.Empty(result.Config.Feeds);
        Assert.Null(result.ETag);
    }

    [Fact]
    public async Task Save_then_Load_round_trips_polymorphic_subscriptions()
    {
        var storage = Substitute.For<IFileStorage>();
        string? captured = null;
        storage.WriteIfMatch(Key, Arg.Do<string>(s => captured = s), null, Arg.Any<CancellationToken>())
            .Returns("etag-1");
        var store = new CollectionConfigStore(storage);

        var config = new CollectionConfig(
        [
            new TickSubscription("BTCUSDT", "binance", DataFeedRole.Primary),
            new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, AlgoTradeForge.Domain.Strategy.TimeFrame.Parse("1m")),
        ]);

        var etag = await store.Save(config, expectedETag: null);
        Assert.Equal("etag-1", etag);
        Assert.NotNull(captured);

        // Feed the captured JSON back through Load to prove the polymorphic round-trip.
        storage.ReadWithEtag(Key, Arg.Any<CancellationToken>())
            .Returns(new StoredObject(captured!, "etag-1"));
        var loaded = await store.Load();

        Assert.Equal(2, loaded.Config.Feeds.Count);
        Assert.IsType<TickSubscription>(loaded.Config.Feeds[0]);
        var tb = Assert.IsType<TimeBarSubscription>(loaded.Config.Feeds[1]);
        Assert.Equal("1m", tb.TimeFrame.Code);
        Assert.Equal("etag-1", loaded.ETag);
    }

    [Fact]
    public async Task Save_propagates_ConcurrencyConflictException_on_stale_etag()
    {
        var storage = Substitute.For<IFileStorage>();
        storage.WriteIfMatch(Key, Arg.Any<string>(), "stale", Arg.Any<CancellationToken>())
            .Throws(new ConcurrencyConflictException(Key, "stale", "current"));
        var store = new CollectionConfigStore(storage);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => store.Save(new CollectionConfig([]), expectedETag: "stale"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter CollectionConfigStoreTests`
Expected: FAIL — `CollectionConfig` / `ICollectionConfigStore` / `CollectionConfigStore` do not exist (compile error).

- [ ] **Step 3: Create the config + interface types**

`src/AlgoTradeForge.LiveHost.Application/Collection/CollectionConfig.cs`:

```csharp
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.LiveHost.Application.Collection;

/// <summary>What this LiveHost captures: a set of root data-feed subscriptions.</summary>
public sealed record CollectionConfig(IReadOnlyList<DataFeedSubscription> Feeds);
```

`src/AlgoTradeForge.LiveHost.Application/Collection/ICollectionConfigStore.cs`:

```csharp
namespace AlgoTradeForge.LiveHost.Application.Collection;

public interface ICollectionConfigStore
{
    Task<StoredCollectionConfig> Load(CancellationToken ct = default);
    Task<string> Save(CollectionConfig config, string? expectedETag, CancellationToken ct = default);
}

public sealed record StoredCollectionConfig(CollectionConfig Config, string? ETag);
```

- [ ] **Step 4: Create the store implementation**

`src/AlgoTradeForge.LiveHost.Infrastructure/Collection/CollectionConfigStore.cs`:

```csharp
using System.Text.Json;
using AlgoTradeForge.LiveHost.Application.Collection;
using AlgoTradeForge.Storage;

namespace AlgoTradeForge.LiveHost.Infrastructure.Collection;

public sealed class CollectionConfigStore(IFileStorage storage) : ICollectionConfigStore
{
    internal const string Key = "collection.json";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<StoredCollectionConfig> Load(CancellationToken ct = default)
    {
        var stored = await storage.ReadWithEtag(Key, ct);
        if (stored is null)
            return new StoredCollectionConfig(new CollectionConfig([]), null);

        var config = JsonSerializer.Deserialize<CollectionConfig>(stored.Content, JsonOpts)
            ?? new CollectionConfig([]);
        return new StoredCollectionConfig(config, stored.ETag);
    }

    public async Task<string> Save(CollectionConfig config, string? expectedETag, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(config, JsonOpts);
        return await storage.WriteIfMatch(Key, json, expectedETag, ct);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter CollectionConfigStoreTests`
Expected: PASS (3 tests).

- [ ] **Step 6: Controller checkpoint** — report green; controller stages + commits.

---

### Task 2: `/api/v1/config` endpoints + DI registration

**Files:**
- Modify: `src/AlgoTradeForge.LiveHost.WebApi/LiveHostServiceCollectionExtensions.cs` (register the store)
- Create: `src/AlgoTradeForge.LiveHost.WebApi/Endpoints/ConfigEndpoints.cs`
- Modify: `src/AlgoTradeForge.LiveHost.WebApi/Program.cs:159` (map the endpoints)
- Test: `tests/AlgoTradeForge.LiveHost.WebApi.Tests/ConfigEndpointsTests.cs`

**Interfaces:**
- Consumes: `ICollectionConfigStore` (Task 1); `ConcurrencyConflictException` (`AlgoTradeForge.Storage`).
- Produces: `ConfigEndpoints.MapConfigEndpoints(this IEndpointRouteBuilder)`; routes `GET /api/v1/config`, `PUT /api/v1/config`.

- [ ] **Step 1: Write the failing test**

Create `tests/AlgoTradeForge.LiveHost.WebApi.Tests/ConfigEndpointsTests.cs`. This isolates the HTTP↔store mapping by substituting `ICollectionConfigStore`; the store's CAS is covered in Task 1.

```csharp
using System.Net;
using System.Net.Http.Json;
using AlgoTradeForge.LiveHost.Application.Collection;
using AlgoTradeForge.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AlgoTradeForge.LiveHost.WebApi.Tests;

public class ConfigEndpointsTests
{
    private static WebApplicationFactory<Program> FactoryWith(ICollectionConfigStore store) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll(typeof(ICollectionConfigStore));
                s.AddSingleton(store);
            }));

    [Fact]
    public async Task Get_returns_200_with_body_and_etag_header()
    {
        var store = Substitute.For<ICollectionConfigStore>();
        store.Load(Arg.Any<CancellationToken>())
            .Returns(new StoredCollectionConfig(new CollectionConfig([]), "etag-1"));
        using var client = FactoryWith(store).CreateClient();

        var resp = await client.GetAsync("/api/v1/config");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("etag-1", resp.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task Get_returns_200_without_etag_when_absent()
    {
        var store = Substitute.For<ICollectionConfigStore>();
        store.Load(Arg.Any<CancellationToken>())
            .Returns(new StoredCollectionConfig(new CollectionConfig([]), null));
        using var client = FactoryWith(store).CreateClient();

        var resp = await client.GetAsync("/api/v1/config");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Null(resp.Headers.ETag);
    }

    [Fact]
    public async Task Put_returns_409_on_stale_etag()
    {
        var store = Substitute.For<ICollectionConfigStore>();
        store.Save(Arg.Any<CollectionConfig>(), "stale", Arg.Any<CancellationToken>())
            .Throws(new ConcurrencyConflictException("collection.json", "stale", "current"));
        using var client = FactoryWith(store).CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Put, "/api/v1/config")
        {
            Content = JsonContent.Create(new CollectionConfig([])),
        };
        req.Headers.TryAddWithoutValidation("If-Match", "stale");

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Put_returns_200_and_new_etag_on_success()
    {
        var store = Substitute.For<ICollectionConfigStore>();
        store.Save(Arg.Any<CollectionConfig>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("etag-2");
        using var client = FactoryWith(store).CreateClient();

        var resp = await client.PutAsJsonAsync("/api/v1/config", new CollectionConfig([]));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("etag-2", resp.Headers.ETag?.Tag);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.WebApi.Tests/ --filter ConfigEndpointsTests`
Expected: FAIL — route `/api/v1/config` not found (404), and `MapConfigEndpoints` does not exist (may also be a compile error on `RemoveAll`; add `using Microsoft.Extensions.DependencyInjection.Extensions;` if needed).

- [ ] **Step 3: Register the store in DI**

In `src/AlgoTradeForge.LiveHost.WebApi/LiveHostServiceCollectionExtensions.cs`, add the using and registration:

```csharp
using AlgoTradeForge.LiveHost.Application.Collection;
using AlgoTradeForge.LiveHost.Infrastructure.Collection;
```

Inside `AddLiveHost`, before `return services;`:

```csharp
        services.AddSingleton<ICollectionConfigStore, CollectionConfigStore>();
```

- [ ] **Step 4: Create the endpoints**

`src/AlgoTradeForge.LiveHost.WebApi/Endpoints/ConfigEndpoints.cs`:

```csharp
using AlgoTradeForge.LiveHost.Application.Collection;
using AlgoTradeForge.Storage;

namespace AlgoTradeForge.LiveHost.WebApi.Endpoints;

public static class ConfigEndpoints
{
    public static void MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/config").WithTags("Collection Config");

        group.MapGet("/", GetConfig)
            .WithName("GetCollectionConfig")
            .WithSummary("Get the collection config (what this host captures)")
            .WithOpenApi();

        group.MapPut("/", PutConfig)
            .WithName("PutCollectionConfig")
            .WithSummary("Replace the collection config (CAS via If-Match)")
            .WithOpenApi();
    }

    private static async Task<IResult> GetConfig(
        ICollectionConfigStore store, HttpResponse response, CancellationToken ct)
    {
        var stored = await store.Load(ct);
        if (stored.ETag is not null)
            response.Headers.ETag = stored.ETag;
        return Results.Json(stored.Config);
    }

    private static async Task<IResult> PutConfig(
        CollectionConfig config, ICollectionConfigStore store, HttpRequest request,
        HttpResponse response, CancellationToken ct)
    {
        // If-Match absent or "*" => create-only (expectedETag null).
        var ifMatch = request.Headers.IfMatch.ToString();
        var expectedETag = string.IsNullOrEmpty(ifMatch) || ifMatch == "*" ? null : ifMatch;

        try
        {
            var newETag = await store.Save(config, expectedETag, ct);
            response.Headers.ETag = newETag;
            return Results.Json(config);
        }
        catch (ConcurrencyConflictException)
        {
            return Results.Conflict("collection.json was modified concurrently; re-GET and retry.");
        }
    }
}
```

Note: a malformed JSON body is rejected by the minimal-API model binder with 400 before the handler runs, satisfying the spec's "400 on malformed body".

- [ ] **Step 5: Map the endpoints**

In `src/AlgoTradeForge.LiveHost.WebApi/Program.cs`, after `app.MapLiveEndpoints();` (line 159):

```csharp
app.MapConfigEndpoints();
```

(The `AlgoTradeForge.LiveHost.WebApi.Endpoints` namespace is already imported at line 15.)

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.WebApi.Tests/ --filter ConfigEndpointsTests`
Expected: PASS (4 tests). If `Microsoft.AspNetCore.Mvc.Testing` is missing from the test csproj, add it (`dotnet add tests/AlgoTradeForge.LiveHost.WebApi.Tests package Microsoft.AspNetCore.Mvc.Testing`) — the existing `public partial class Program {}` already supports `WebApplicationFactory<Program>`.

- [ ] **Step 7: Controller checkpoint** — report green; controller stages + commits.

---

### Task 3: Relay pump sources its instrument list from `collection.json`

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.WebApi/RelayInstrumentSelector.cs` (pure, testable projection)
- Modify: `src/AlgoTradeForge.LiveHost.WebApi/RelayPumpHostedService.cs` (inject store, derive instruments)
- Modify: `src/AlgoTradeForge.LiveHost.WebApi/RelayPumpOptions.cs` (delete `Instruments`)
- Test: `tests/AlgoTradeForge.LiveHost.WebApi.Tests/RelayInstrumentSelectorTests.cs`

**Interfaces:**
- Consumes: `CollectionConfig` (Task 1), `TickSubscription` (Domain).
- Produces: `static string[] RelayInstrumentSelector.StreamableInstruments(CollectionConfig config)` — distinct `AssetName`s of `Tick` feeds.

- [ ] **Step 1: Write the failing test**

Create `tests/AlgoTradeForge.LiveHost.WebApi.Tests/RelayInstrumentSelectorTests.cs`:

```csharp
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Application.Collection;
using AlgoTradeForge.LiveHost.WebApi;

namespace AlgoTradeForge.LiveHost.WebApi.Tests;

public class RelayInstrumentSelectorTests
{
    [Fact]
    public void Selects_distinct_tick_asset_names_only()
    {
        var config = new CollectionConfig(
        [
            new TickSubscription("BTCUSDT", "binance", DataFeedRole.Primary),
            new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1m")),
            new TickSubscription("ETHUSDT", "binance", DataFeedRole.Primary),
            new TickSubscription("BTCUSDT", "binance", DataFeedRole.Primary), // dup
        ]);

        var instruments = RelayInstrumentSelector.StreamableInstruments(config);

        Assert.Equal(["BTCUSDT", "ETHUSDT"], instruments);
    }

    [Fact]
    public void Empty_when_no_tick_feeds()
    {
        var config = new CollectionConfig(
            [new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1m"))]);

        Assert.Empty(RelayInstrumentSelector.StreamableInstruments(config));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.WebApi.Tests/ --filter RelayInstrumentSelectorTests`
Expected: FAIL — `RelayInstrumentSelector` does not exist (compile error).

- [ ] **Step 3: Create the selector**

`src/AlgoTradeForge.LiveHost.WebApi/RelayInstrumentSelector.cs`:

```csharp
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Application.Collection;

namespace AlgoTradeForge.LiveHost.WebApi;

/// <summary>Projects the collection config to the relay's streamable instrument list.</summary>
public static class RelayInstrumentSelector
{
    // Today only Tick feeds are streamed by the relay (trades). book-ticker/quotes are future.
    public static string[] StreamableInstruments(CollectionConfig config) =>
        config.Feeds
            .OfType<TickSubscription>()
            .Select(t => t.AssetName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
```

- [ ] **Step 4: Run the selector test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.WebApi.Tests/ --filter RelayInstrumentSelectorTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Delete `RelayPumpOptions.Instruments` and wire the relay to the store**

In `src/AlgoTradeForge.LiveHost.WebApi/RelayPumpOptions.cs`, delete the `Instruments` line so the class is:

```csharp
namespace AlgoTradeForge.LiveHost.WebApi;

public sealed class RelayPumpOptions
{
    public string LocalRoot { get; set; } = "relay-segments";
    public string KeyPrefix { get; set; } = "live-md";
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan UploadInterval { get; set; } = TimeSpan.FromSeconds(60);
}
```

In `src/AlgoTradeForge.LiveHost.WebApi/RelayPumpHostedService.cs`, add `ICollectionConfigStore collectionStore` to the primary constructor (add `using AlgoTradeForge.LiveHost.Application.Collection;`), and replace `ExecuteAsync` so it derives the instrument list from the store:

```csharp
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var stored = await collectionStore.Load(ct).ConfigureAwait(false);
        var instruments = RelayInstrumentSelector.StreamableInstruments(stored.Config);
        if (instruments.Length == 0)
        {
            logger.LogInformation("RelayPumpHostedService: no streamable instruments in collection.json, skipping relay pump.");
            return;
        }
        try
        {
            await RunPumpOnce(instruments, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("RelayPumpHostedService shutting down.");
        }
    }
```

`RunPumpOnce(IReadOnlyList<string>, CancellationToken)` is unchanged (it already takes the instrument list). Remove the now-unused `IOptions<RelayPumpOptions>`-based `o.Instruments` read in `ExecuteAsync` only; `RunPumpOnce` still reads `opts.Value` for `LocalRoot`/`KeyPrefix`/intervals.

- [ ] **Step 6: Build to verify the deletion compiles**

Run: `dotnet build AlgoTradeForge.slnx`
Expected: SUCCESS. If any appsettings binding or test referenced `RelayPumpOptions.Instruments`, the compiler/build will flag it — remove those references (owner directive: no shims). Search and update: `Grep "RelayPump" appsettings*.json` — drop any `"Instruments"` key under `RelayPump`.

- [ ] **Step 7: Run the WebApi test suite**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.WebApi.Tests/`
Expected: PASS (existing relay tests + new selector tests). If an existing relay test seeded `RelayPumpOptions.Instruments`, rewrite it to assert through `RelayInstrumentSelector` / a substitute `ICollectionConfigStore`.

- [ ] **Step 8: Controller checkpoint** — report green; controller stages + commits.

---

### Task 4: Gate session start on execution-⊆-collected

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Application/Collection/CollectionCoverage.cs`
- Modify: `src/AlgoTradeForge.LiveHost.Application/Live/StartLiveSessionCommandHandler.cs` (inject store, validate)
- Test: `tests/AlgoTradeForge.LiveHost.Application.Tests/Collection/CollectionCoverageTests.cs`

**Interfaces:**
- Consumes: `CollectionConfig` (Task 1); `DataFeedSubscription` subtypes + `AltBarFeedId` (`AlgoTradeForge.Domain.Aggregation`).
- Produces: `static string? CollectionCoverage.FindUnmet(IReadOnlyList<DataFeedSubscription> collected, IEnumerable<DataFeedSubscription> required)` — null when all satisfied, else a message naming the first unmet (asset, feed).

- [ ] **Step 1: Write the failing test**

Create `tests/AlgoTradeForge.LiveHost.Application.Tests/Collection/CollectionCoverageTests.cs`:

```csharp
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Application.Collection;

namespace AlgoTradeForge.LiveHost.Application.Tests.Collection;

public class CollectionCoverageTests
{
    private static readonly DataFeedSubscription[] Collected =
    [
        new TickSubscription("BTCUSDT", "binance", DataFeedRole.Primary),
        new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1m")),
        new SideFeedSubscription("BTCUSDT", "binance", DataFeedRole.Side, "funding-rate"),
    ];

    [Fact]
    public void Null_when_tick_collected()
    {
        var required = new[] { new TickSubscription("BTCUSDT", "binance", DataFeedRole.Primary) };
        Assert.Null(CollectionCoverage.FindUnmet(Collected, required));
    }

    [Fact]
    public void Reports_when_tick_not_collected()
    {
        var required = new[] { new TickSubscription("ETHUSDT", "binance", DataFeedRole.Primary) };
        var unmet = CollectionCoverage.FindUnmet(Collected, required);
        Assert.NotNull(unmet);
        Assert.Contains("ETHUSDT", unmet);
    }

    [Fact]
    public void Null_when_timebar_interval_matches()
    {
        var required = new[] { new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1m")) };
        Assert.Null(CollectionCoverage.FindUnmet(Collected, required));
    }

    [Fact]
    public void Reports_when_timebar_interval_differs()
    {
        var required = new[] { new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1h")) };
        Assert.NotNull(CollectionCoverage.FindUnmet(Collected, required));
    }

    [Fact]
    public void Null_when_sidefeed_collected()
    {
        var required = new[] { new SideFeedSubscription("BTCUSDT", "binance", DataFeedRole.Side, "funding-rate") };
        Assert.Null(CollectionCoverage.FindUnmet(Collected, required));
    }

    [Fact]
    public void AltBar_validates_against_tick_root()
    {
        // EqV_ticks_1000 derives from the collected Tick root -> satisfied.
        var required = new[] { new AltBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, "EqV_ticks_1000") };
        Assert.Null(CollectionCoverage.FindUnmet(Collected, required));
    }

    [Fact]
    public void AltBar_validates_against_candle_root()
    {
        // EqV_1m_1000 derives from the collected 1m candle root -> satisfied.
        var required = new[] { new AltBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, "EqV_1m_1000") };
        Assert.Null(CollectionCoverage.FindUnmet(Collected, required));

        // EqV_5m_1000 needs a 5m candle root we did not collect -> reported.
        var missing = new[] { new AltBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, "EqV_5m_1000") };
        Assert.NotNull(CollectionCoverage.FindUnmet(Collected, missing));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/ --filter CollectionCoverageTests`
Expected: FAIL — `CollectionCoverage` does not exist (compile error).

- [ ] **Step 3: Implement the coverage helper**

`src/AlgoTradeForge.LiveHost.Application/Collection/CollectionCoverage.cs`:

```csharp
using AlgoTradeForge.Domain.Aggregation;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.LiveHost.Application.Collection;

/// <summary>
/// Validates that each required strategy subscription is backed by a collected root feed.
/// Matching ignores Role/Asset; alt-bars resolve to their source root via AltBarFeedId.
/// </summary>
public static class CollectionCoverage
{
    public static string? FindUnmet(
        IReadOnlyList<DataFeedSubscription> collected,
        IEnumerable<DataFeedSubscription> required)
    {
        foreach (var r in required)
        {
            if (!IsSatisfied(collected, r))
                return Describe(r);
        }
        return null;
    }

    private static bool IsSatisfied(IReadOnlyList<DataFeedSubscription> collected, DataFeedSubscription r) => r switch
    {
        TickSubscription => collected.OfType<TickSubscription>().Any(c => SameAsset(c, r)),
        TimeBarSubscription tb => collected.OfType<TimeBarSubscription>().Any(c => SameAsset(c, r) && c.TimeFrame == tb.TimeFrame),
        SideFeedSubscription sf => collected.OfType<SideFeedSubscription>().Any(c => SameAsset(c, r) && c.FeedId == sf.FeedId),
        AltBarSubscription ab => AltBarRootSatisfied(collected, ab),
        _ => false,
    };

    private static bool AltBarRootSatisfied(IReadOnlyList<DataFeedSubscription> collected, AltBarSubscription ab)
    {
        var source = AltBarFeedId.Parse(ab.FeedId).SourceCode; // "ticks" or a candle interval e.g. "1m"
        return source == "ticks"
            ? collected.OfType<TickSubscription>().Any(c => SameAsset(c, ab))
            : collected.OfType<TimeBarSubscription>().Any(c => SameAsset(c, ab) && c.TimeFrame.Code == source);
    }

    private static bool SameAsset(DataFeedSubscription a, DataFeedSubscription b) =>
        string.Equals(a.AssetName, b.AssetName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Exchange, b.Exchange, StringComparison.OrdinalIgnoreCase);

    private static string Describe(DataFeedSubscription r) => r switch
    {
        TimeBarSubscription tb => $"{r.AssetName}@{r.Exchange} time-bar {tb.TimeFrame.Code}",
        AltBarSubscription ab => $"{r.AssetName}@{r.Exchange} alt-bar {ab.FeedId} (root '{AltBarFeedId.Parse(ab.FeedId).SourceCode}')",
        SideFeedSubscription sf => $"{r.AssetName}@{r.Exchange} side feed '{sf.FeedId}'",
        _ => $"{r.AssetName}@{r.Exchange} {r.KindOf()}",
    };
}
```

Note: time-bar matching is **exact interval** (the spec's minimal acceptable impl); finer-divisor derivation is a documented future enhancement.

- [ ] **Step 4: Run the coverage test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/ --filter CollectionCoverageTests`
Expected: PASS (7 assertions across the facts).

- [ ] **Step 5: Wire validation into the handler**

In `src/AlgoTradeForge.LiveHost.Application/Live/StartLiveSessionCommandHandler.cs`:

Add `using AlgoTradeForge.LiveHost.Application.Collection;`, add `ICollectionConfigStore collectionStore` to the primary constructor parameter list:

```csharp
public sealed class StartLiveSessionCommandHandler(
    IStrategyFactory strategyFactory,
    ILiveAccountManager accountManager,
    ILiveSessionStore sessionStore,
    IAssetRepository assetRepository,
    IOptimizationSpaceProvider spaceProvider,
    ICollectionConfigStore collectionStore) : ICommandHandler<StartLiveSessionCommand, LiveSessionSubmissionDto>
```

After the `resolvedSubscriptions` loop completes (after line 34, before `ResolveExecutionAsset`):

```csharp
        var collected = await collectionStore.Load(ct);
        var unmet = CollectionCoverage.FindUnmet(collected.Config.Feeds, resolvedSubscriptions);
        if (unmet is not null)
            throw new ArgumentException($"Cannot execute on uncollected feed: {unmet}. Add it to collection.json first.");
```

- [ ] **Step 6: Update existing handler test fixtures**

Existing `StartLiveSessionCommandHandler` tests construct the handler directly and will not compile (new ctor param). For each, add a substitute store whose `Load` returns a config covering that test's subscriptions:

```csharp
var collectionStore = Substitute.For<ICollectionConfigStore>();
collectionStore.Load(Arg.Any<CancellationToken>())
    .Returns(new StoredCollectionConfig(new CollectionConfig(testSubscriptions), "etag"));
```

and pass `collectionStore` as the final ctor arg. (`testSubscriptions` = the same `DataFeedSubscription`s the test submits.) Add at least one new test asserting an uncollected subscription throws `ArgumentException`.

- [ ] **Step 7: Run the Application test suite**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/`
Expected: PASS (coverage tests + updated/new handler tests).

- [ ] **Step 8: Controller checkpoint** — report green; controller stages + commits.

---

### Final verification (controller, after Task 4)

- [ ] Run the full LiveHost suite sequentially:
  - `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/`
  - `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/`
  - `dotnet test tests/AlgoTradeForge.LiveHost.WebApi.Tests/`
- [ ] Confirm `RelayPumpOptions.Instruments` is gone (`Grep "Instruments" src/AlgoTradeForge.LiveHost.WebApi`) and no appsettings key references it.
- [ ] Whole-branch review (opus) against the spec; backtest golden suites untouched (no Domain/engine change) so no benchmark run required.

## Self-Review (against the spec)

**Spec coverage:** config model = `CollectionConfig(List<DataFeedSubscription>)` (Task 1); CAS store (Task 1); `GET/PUT /api/v1/config` with ETag/If-Match→200/409/400 (Task 2); relay sources instrument list from collected Tick feeds + `RelayPumpOptions.Instruments` deleted (Task 3); execution-⊆-collected validation incl. alt-bar→root (Task 4); IB forward-compat needs no code (reuse = the story). No Domain change (Option Y) — consistent throughout. All spec sections map to a task.

**Placeholder scan:** every code step shows complete code; commands have expected output; no TBD/TODO.

**Type consistency:** `ICollectionConfigStore.Load`/`Save`, `CollectionConfig.Feeds`, `StoredCollectionConfig(Config, ETag)`, `RelayInstrumentSelector.StreamableInstruments`, `CollectionCoverage.FindUnmet` are referenced with identical signatures across tasks. `TimeFrame.Code`/`TimeFrame.Parse`, `AltBarFeedId.Parse(...).SourceCode`, `IFileStorage.ReadWithEtag`/`WriteIfMatch`, `StoredObject(Content, ETag)`, `ConcurrencyConflictException` match the verified source.
