using System.Text;
using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Groups;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain.Symbology;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;
using AlgoTradeForge.HistoryLoader.WebApi.Endpoints;
using AlgoTradeForge.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Groups;

public sealed class GroupEndpointsTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("atf-grp-ep-").FullName;
    private GroupStore _store = null!;
    private SqliteHistoryIndex _index = null!;
    private readonly SymbologyRegistry _registry = new([new BinanceSymbology()]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = new GroupStore(
            new LocalFileStorage(),
            Options.Create(new HistoryLoaderOptions { ConfigRoot = _dir }),
            NullLogger<GroupStore>.Instance);

        var init = new HistoryIndexInitializer(Path.Combine(_dir, "idx.sqlite"));
        await init.EnsureCreated(Ct);
        _index = new SqliteHistoryIndex(init, init.ConnectionString + ";Pooling=False");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // ---- helpers ----

    // Handlers return Results.Json (typed object) or Results.NoContent.
    // IValueHttpResult (non-generic) exposes the typed object without calling ExecuteAsync.
    // For typed anonymous objects whose properties are snake_case identifiers,
    // JsonSerializer.Serialize(value) with default options preserves those names.
    private static (int Status, JsonElement? Body) Inspect(IResult result)
    {
        var statusCode = (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
        var value = (result as IValueHttpResult)?.Value;
        if (value is null) return (statusCode, null);

        // Pre-serialized string (ContentHttpResult) → parse directly.
        // Typed object (JsonHttpResult<T>) → re-serialize with default options.
        var json = value as string ?? JsonSerializer.Serialize(value);
        using var doc = JsonDocument.Parse(json);
        return (statusCode, doc.RootElement.Clone());
    }

    private static HttpRequest BodyOf(CollectionGroup group, string? ifMatch = null)
    {
        var ctx   = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(group, GroupJson.Options));
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentType = "application/json";
        if (ifMatch is not null)
            ctx.Request.Headers["If-Match"] = ifMatch;
        return ctx.Request;
    }

    private static CollectionGroup Valid(string name) => new(
        Name:            name,
        Enabled:         true,
        Exchanges:       ["binance"],
        Assets:          new GroupAssets(["BTC/USDT-PERP"], "2023-01"),
        Feeds:           new Dictionary<string, GroupFeed>
        {
            ["candles"] = new GroupFeed("eager", ["1h"], null),
        },
        Derived:         null,
        SymbolOverrides: null);

    private static CollectionGroup WithFormat(string name, string format) => new(
        Name:            name,
        Enabled:         true,
        Exchanges:       ["binance"],
        Assets:          new GroupAssets(["BTC/USDT-PERP"], "2023-01"),
        Feeds:           new Dictionary<string, GroupFeed>
        {
            ["candles"] = new GroupFeed("eager", ["1h"], format),
        },
        Derived:         null,
        SymbolOverrides: null);

    // =========================================================================
    // GET /groups
    // =========================================================================

    [Fact]
    public async Task GetGroups_Empty_ReturnsEmptyList()
    {
        var result = await GroupEndpoints.GetGroups(_store, Ct);
        var (status, body) = Inspect(result);

        Assert.Equal(200, status);
        Assert.Equal(0, body!.Value.GetProperty("groups").GetArrayLength());
    }

    [Fact]
    public async Task GetGroups_WithGroup_ReturnsSummary()
    {
        await _store.Put("my-group", Valid("my-group"), null, Ct);

        var result = await GroupEndpoints.GetGroups(_store, Ct);
        var (status, body) = Inspect(result);

        Assert.Equal(200, status);
        var arr = body!.Value.GetProperty("groups");
        Assert.Equal(1, arr.GetArrayLength());
        var s = arr[0];
        Assert.Equal("my-group", s.GetProperty("name").GetString());
        Assert.True(s.GetProperty("enabled").GetBoolean());
        Assert.Equal(1, s.GetProperty("symbol_count").GetInt32());
        Assert.Equal(1, s.GetProperty("feed_count").GetInt32());
        Assert.NotEmpty(s.GetProperty("etag").GetString()!);
    }

    // =========================================================================
    // GET /groups/{name}
    // =========================================================================

    [Fact]
    public async Task GetGroup_Exists_ReturnsGroupBodyAndETagHeader()
    {
        var etag = await _store.Put("alpha", Valid("alpha"), null, Ct);

        // GetGroup sets ctx.Response.Headers["ETag"] as a side-effect before returning IResult.
        var ctx = new DefaultHttpContext();
        var result = await GroupEndpoints.GetGroup("alpha", _store, ctx, Ct);

        // ETag header must be set before result is processed.
        Assert.Equal(etag, ctx.Response.Headers["ETag"].ToString());

        // Status
        Assert.Equal(200, (result as IStatusCodeHttpResult)?.StatusCode ?? 200);

        // Group document body: Results.Json(group, GroupJson.Options) stores the CollectionGroup
        // as the typed IValueHttpResult.Value. Access it directly to verify camelCase properties.
        var group = (result as IValueHttpResult<CollectionGroup>)?.Value;
        Assert.NotNull(group);
        Assert.Equal("alpha", group.Name);
        Assert.Equal("2023-01", group.Assets.HistoryStart);
    }

    [Fact]
    public async Task GetGroup_NotFound_Returns404()
    {
        var result = await GroupEndpoints.GetGroup("no-such", _store, new DefaultHttpContext(), Ct);
        var (status, body) = Inspect(result);

        Assert.Equal(404, status);
        Assert.Equal("group_not_found", body!.Value.GetProperty("error").GetString());
        Assert.Equal("no-such", body.Value.GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetGroup_CorruptJson_Returns422()
    {
        // Write corrupt JSON directly into the store's groups dir
        var groupsDir = Path.Combine(_dir, "groups");
        Directory.CreateDirectory(groupsDir);
        await File.WriteAllTextAsync(Path.Combine(groupsDir, "corrupt.json"), "{not-json{{", Ct);

        var result = await GroupEndpoints.GetGroup("corrupt", _store, new DefaultHttpContext(), Ct);
        var (status, body) = Inspect(result);

        Assert.Equal(422, status);
        Assert.Equal("validation_failed", body!.Value.GetProperty("error").GetString());
        Assert.True(body.Value.GetProperty("errors").GetArrayLength() > 0);
    }

    // =========================================================================
    // PUT /groups/{name}
    // =========================================================================

    [Fact]
    public async Task PutGroup_Create_Returns200WithETag()
    {
        var result = await GroupEndpoints.PutGroup("beta", BodyOf(Valid("beta")), _store, Ct);
        var (status, body) = Inspect(result);

        Assert.Equal(200, status);
        Assert.NotEmpty(body!.Value.GetProperty("etag").GetString()!);
    }

    [Fact]
    public async Task PutGroup_Update_WithCorrectETag_ReturnsNewETag()
    {
        var etag1 = await _store.Put("gamma", Valid("gamma"), null, Ct);

        var result = await GroupEndpoints.PutGroup(
            "gamma", BodyOf(Valid("gamma") with { Enabled = false }, etag1), _store, Ct);
        var (status, body) = Inspect(result);

        Assert.Equal(200, status);
        var etag2 = body!.Value.GetProperty("etag").GetString()!;
        Assert.NotEqual(etag1, etag2);
    }

    [Fact]
    public async Task PutGroup_StaleETag_Returns409()
    {
        await _store.Put("delta", Valid("delta"), null, Ct);

        var result = await GroupEndpoints.PutGroup(
            "delta", BodyOf(Valid("delta"), "stale-etag"), _store, Ct);
        var (status, body) = Inspect(result);

        Assert.Equal(409, status);
        Assert.Equal("concurrency_conflict", body!.Value.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PutGroup_InvalidGroup_Returns422WithErrors()
    {
        var bad = new CollectionGroup(
            Name:            "epsilon",
            Enabled:         true,
            Exchanges:       [],
            Assets:          new GroupAssets([], "bad-date"),
            Feeds:           new Dictionary<string, GroupFeed>(),
            Derived:         null,
            SymbolOverrides: null);

        var result = await GroupEndpoints.PutGroup("epsilon", BodyOf(bad), _store, Ct);
        var (status, body) = Inspect(result);

        Assert.Equal(422, status);
        Assert.Equal("validation_failed", body!.Value.GetProperty("error").GetString());
        Assert.True(body.Value.GetProperty("errors").GetArrayLength() > 0);
    }

    [Fact]
    public async Task PutGroup_NameMismatch_Returns422()
    {
        // body.name = "wrong-name" but path name = "eta"
        var result = await GroupEndpoints.PutGroup(
            "eta", BodyOf(Valid("wrong-name")), _store, Ct);
        var (status, body) = Inspect(result);

        Assert.Equal(422, status);
        Assert.Equal("validation_failed", body!.Value.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PutGroup_InvalidPathName_Returns422()
    {
        // "UPPERCASE" fails the name regex
        var result = await GroupEndpoints.PutGroup(
            "UPPERCASE", BodyOf(Valid("UPPERCASE")), _store, Ct);
        var (status, body) = Inspect(result);

        Assert.Equal(422, status);
        Assert.Equal("validation_failed", body!.Value.GetProperty("error").GetString());
    }

    // =========================================================================
    // DELETE /groups/{name}
    // =========================================================================

    [Fact]
    public async Task DeleteGroup_Exists_Returns204()
    {
        await _store.Put("theta", Valid("theta"), null, Ct);

        var result = await GroupEndpoints.DeleteGroup("theta", _store, Ct);
        var (status, body) = Inspect(result);

        Assert.Equal(204, status);
        Assert.Null(body);
    }

    [Fact]
    public async Task DeleteGroup_NotFound_Returns404()
    {
        var result = await GroupEndpoints.DeleteGroup("no-such", _store, Ct);
        var (status, body) = Inspect(result);

        Assert.Equal(404, status);
        Assert.Equal("group_not_found", body!.Value.GetProperty("error").GetString());
    }

    // =========================================================================
    // POST /groups/validate
    // =========================================================================

    [Fact]
    public async Task ValidateGroup_HappyPath_ReturnsTupleCountAndPerExchange()
    {
        var group = Valid("iota");

        var result = await GroupEndpoints.ValidateGroup(
            BodyOf(group), _store, _registry, _index, Ct);
        var (status, body) = Inspect(result);

        Assert.Equal(200, status);
        var exp = body!.Value.GetProperty("expansion");
        // BTC/USDT-PERP × binance × candles/1h → 1 tuple
        Assert.Equal(1, exp.GetProperty("tuple_count").GetInt32());
        Assert.Equal(0, exp.GetProperty("unsupported").GetArrayLength());
        Assert.Equal(0, exp.GetProperty("conflicts").GetArrayLength());
        Assert.Equal(0, exp.GetProperty("already_materialized").GetInt32());

        var perEx = exp.GetProperty("per_exchange");
        Assert.Equal(1, perEx.GetArrayLength());
        var binance = perEx[0];
        Assert.Equal("binance", binance.GetProperty("exchange").GetString());
        Assert.Equal(1, binance.GetProperty("symbols").GetInt32());
        Assert.Equal(1, binance.GetProperty("feeds").GetInt32());
    }

    [Fact]
    public async Task ValidateGroup_UnknownExchange_ReturnsUnsupported()
    {
        var group = new CollectionGroup(
            Name:            "kappa",
            Enabled:         true,
            Exchanges:       ["unknownexchange"],
            Assets:          new GroupAssets(["BTC/USDT"], "2023-01"),
            Feeds:           new Dictionary<string, GroupFeed>
            {
                ["candles"] = new GroupFeed("eager", ["1h"], null),
            },
            Derived:         null,
            SymbolOverrides: null);

        var result = await GroupEndpoints.ValidateGroup(
            BodyOf(group), _store, _registry, _index, Ct);
        var (status, body) = Inspect(result);

        Assert.Equal(200, status);
        var unsupported = body!.Value.GetProperty("expansion").GetProperty("unsupported");
        Assert.True(unsupported.GetArrayLength() > 0);
        Assert.Equal("unknownexchange", unsupported[0].GetProperty("exchange").GetString());
    }

    [Fact]
    public async Task ValidateGroup_ConflictWithStoredGroup_ReturnsConflict()
    {
        // Store g1 with csv format
        await _store.Put("g1", WithFormat("g1", "csv"), null, Ct);

        // Submit g2 with parquet format for the same feed+symbol → format conflict
        var g2 = WithFormat("g2", "parquet");

        var result = await GroupEndpoints.ValidateGroup(
            BodyOf(g2), _store, _registry, _index, Ct);
        var (status, body) = Inspect(result);

        Assert.Equal(200, status);
        var conflicts = body!.Value.GetProperty("expansion").GetProperty("conflicts");
        Assert.True(conflicts.GetArrayLength() > 0);
    }

    [Fact]
    public async Task ValidateGroup_SameNameExclusion_EditDoesNotConflictWithItself()
    {
        // Store g1 with csv format
        await _store.Put("g1", WithFormat("g1", "csv"), null, Ct);

        // Submit g1 with parquet format (editing) — stored g1 is EXCLUDED, so no conflict
        var edited = WithFormat("g1", "parquet");

        var result = await GroupEndpoints.ValidateGroup(
            BodyOf(edited), _store, _registry, _index, Ct);
        var (status, body) = Inspect(result);

        Assert.Equal(200, status);
        var conflicts = body!.Value.GetProperty("expansion").GetProperty("conflicts");
        Assert.Equal(0, conflicts.GetArrayLength());
    }

    [Fact]
    public async Task ValidateGroup_DisabledDraft_StillPreviewsTuples()
    {
        // Disabled group: without forcing Enabled=true, expansion returns 0 tuples
        var draft = Valid("lambda") with { Enabled = false };

        var result = await GroupEndpoints.ValidateGroup(
            BodyOf(draft), _store, _registry, _index, Ct);
        var (status, body) = Inspect(result);

        Assert.Equal(200, status);
        // Endpoint must force Enabled=true, so tuples must be non-zero
        Assert.True(
            body!.Value.GetProperty("expansion").GetProperty("tuple_count").GetInt32() > 0,
            "disabled draft should still preview its tuples (Enabled forced true)");
    }

    [Fact]
    public async Task ValidateGroup_AlreadyMaterialized_CountsIndexedTuples()
    {
        // Seed the index: binance / BTCUSDT_perp / candles / 1h
        await _index.UpsertFeedStatus(
            new FeedStatusIndexRow(
                "binance", "BTCUSDT_perp", "candles", "1h",
                null, null, 0, "Healthy", "[]", "[]"),
            Ct);

        var group = Valid("mu");

        var result = await GroupEndpoints.ValidateGroup(
            BodyOf(group), _store, _registry, _index, Ct);
        var (status, body) = Inspect(result);

        Assert.Equal(200, status);
        Assert.Equal(1,
            body!.Value.GetProperty("expansion").GetProperty("already_materialized").GetInt32());
    }

    [Fact]
    public async Task ValidateGroup_NonCandles_AtCadenceInterval_CountsAlreadyMaterialized()
    {
        // mark-price tuple has Interval "" from GroupExpansion, but index row is at "1h" (cadence interval).
        // already_materialized must count it (F1 fix).
        await _index.UpsertAsset(
            new AssetIndexRow("binance", "BTCUSDT_perp", "BTCUSDT", "CryptoPerpetual", "{}"), Ct);
        await _index.ReplaceMonths("binance", "BTCUSDT_perp", "mark-price", "1h",
            [new MonthPartitionRow("2024-01", 10, 1, "mt")], Ct);

        var group = new CollectionGroup(
            Name:            "nu",
            Enabled:         true,
            Exchanges:       ["binance"],
            Assets:          new GroupAssets(["BTC/USDT-PERP"], "2023-01"),
            Feeds:           new Dictionary<string, GroupFeed>
            {
                ["mark-price"] = new GroupFeed("eager", null, null),
            },
            Derived:         null,
            SymbolOverrides: null);

        var result = await GroupEndpoints.ValidateGroup(
            BodyOf(group), _store, _registry, _index, Ct);
        var (status, body) = Inspect(result);

        Assert.Equal(200, status);
        Assert.Equal(1,
            body!.Value.GetProperty("expansion").GetProperty("already_materialized").GetInt32());
    }
}
