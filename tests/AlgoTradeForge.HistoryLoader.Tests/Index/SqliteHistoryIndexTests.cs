using Microsoft.Data.Sqlite;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Index;

public sealed class SqliteHistoryIndexTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("atf-index-").FullName;
    private SqliteHistoryIndex _index = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var init = new HistoryIndexInitializer(Path.Combine(_dir, "idx.sqlite"));
        await init.EnsureCreated(Ct);
        // Pooling=False so Dispose can delete the temp dir on Windows.
        _index = new SqliteHistoryIndex(init, init.ConnectionString + ";Pooling=False");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task UpsertAsset_ThenGet_RoundTrips()
    {
        var row = new AssetIndexRow("binance", "BTCUSDT_perp", "BTCUSDT", "CryptoPerpetual", """{"feeds":{}}""");
        await _index.UpsertAsset(row, Ct);
        await _index.UpsertAsset(row with { Type = "Crypto" }, Ct);   // second upsert overwrites

        var fetched = await _index.GetAsset("binance", "BTCUSDT_perp", Ct);
        Assert.NotNull(fetched);
        Assert.Equal("Crypto", fetched!.Type);
        Assert.Single(await _index.ListAssets(ct: Ct));
        Assert.False(await _index.IsEmpty(Ct));
    }

    [Fact]
    public async Task ListAssets_FiltersByExchange_CaseInsensitive()
    {
        await _index.UpsertAsset(new("binance", "BTCUSDT", "BTCUSDT", "Crypto", "{}"), Ct);
        await _index.UpsertAsset(new("nasdaq", "NFLX", "NFLX", "Equity", "{}"), Ct);

        Assert.Single(await _index.ListAssets("BINANCE", Ct));
        Assert.Equal(2, (await _index.ListAssets(ct: Ct)).Count);
    }

    [Fact]
    public async Task ReplaceMonths_ReplacesWholeFeedSet()
    {
        await _index.ReplaceMonths("binance", "BTCUSDT", "candles", "1h",
            [new("2024-01", 744, 100, "m1"), new("2024-02", 696, 90, "m2")], Ct);
        await _index.ReplaceMonths("binance", "BTCUSDT", "candles", "1h",
            [new("2024-02", 700, 95, "m3")], Ct);

        var months = await _index.GetMonths("binance", "BTCUSDT", "candles", "1h", Ct);
        var only = Assert.Single(months);
        Assert.Equal("2024-02", only.Month);
        Assert.Equal(700, only.Rows);
    }

    [Fact]
    public async Task PruneFeedData_DeletesRowsOutsideKeepSet()
    {
        await _index.UpsertFeedStatus(new("binance", "BTCUSDT", "candles", "1h", 1, 2, 10, "Healthy", "[]", "[]"), Ct);
        await _index.UpsertFeedStatus(new("binance", "BTCUSDT", "mark-price", "1h", 1, 2, 10, "Healthy", "[]", "[]"), Ct);
        await _index.ReplaceMonths("binance", "BTCUSDT", "mark-price", "1h", [new("2024-01", 1, 1, "m")], Ct);

        await _index.PruneFeedData("binance", "BTCUSDT", [("candles", "1h")], Ct);

        var statuses = await _index.GetFeedStatuses("binance", "BTCUSDT", Ct);
        Assert.Single(statuses);
        Assert.Equal("candles", statuses[0].FeedName);
        Assert.Empty(await _index.GetMonths("binance", "BTCUSDT", "mark-price", "1h", Ct));
    }

    [Fact]
    public async Task Jobs_CreateUpdateGet_AndActiveLookup()
    {
        var id = await _index.CreateJob("rebuild", Ct);
        var active = await _index.GetActiveJob("rebuild", Ct);
        Assert.Equal(id, active!.Id);
        Assert.Equal("running", active.State);

        await _index.UpdateJob(id, "completed", progressJson: """{"assets_done":5}""", ct: Ct);
        var job = await _index.GetJob(id, Ct);
        Assert.Equal("completed", job!.State);
        Assert.Null(await _index.GetActiveJob("rebuild", Ct));
        Assert.Equal(id, (await _index.GetLastJob("rebuild", Ct))!.Id);   // latest regardless of state
    }

    [Fact]
    public async Task ListFeedKeys_UnionsStatusAndMonthRows()
    {
        await _index.UpsertFeedStatus(new("binance", "BTCUSDT", "candles", "1h", 1, 2, 10, "Healthy", "[]", "[]"), Ct);
        // Month rows without a status row — the static-equity shape.
        await _index.ReplaceMonths("binance", "BTCUSDT", "candles", "1d", [new("2024-01", 21, 1, "m")], Ct);

        var keys = await _index.ListFeedKeys("binance", "BTCUSDT", Ct);
        Assert.Equal(2, keys.Count);
        Assert.Contains(("candles", "1h"), keys);
        Assert.Contains(("candles", "1d"), keys);
    }

    [Fact]
    public async Task SetDiscoveredFirstMonth_UpsertsBeforeAnyDataWrite()
    {
        await _index.SetDiscoveredFirstMonth("binance", "BTCUSDT_perp", "mark-price", "1h", "2023-05", Ct);
        var rows = await _index.ListDiscoveredFirstMonths(Ct);
        var row = Assert.Single(rows);
        Assert.Equal(("binance", "BTCUSDT_perp", "mark-price", "1h", "2023-05"),
            (row.Exchange, row.Dir, row.FeedName, row.Interval, row.Month));

        // second write overwrites (rediscovery)
        await _index.SetDiscoveredFirstMonth("binance", "BTCUSDT_perp", "mark-price", "1h", "2023-04", Ct);
        Assert.Equal("2023-04", (await _index.ListDiscoveredFirstMonths(Ct))[0].Month);
    }

    [Fact]
    public async Task SetDiscoveredFirstMonth_PreservesExistingStatusColumns()
    {
        await _index.UpsertFeedStatus(new FeedStatusIndexRow(
            "binance", "BTCUSDT_perp", "funding-rate", "", 1L, 2L, 42, "Healthy", "[]", "[\"2024-01\"]"), Ct);
        await _index.SetDiscoveredFirstMonth("binance", "BTCUSDT_perp", "funding-rate", "", "2023-11", Ct);
        var status = Assert.Single(await _index.GetFeedStatuses("binance", "BTCUSDT_perp", Ct));
        Assert.Equal(42, status.RecordCount);          // upsert must not blank existing columns
        Assert.Equal("[\"2024-01\"]", status.CompleteMonthsJson);
    }

    [Fact]
    public async Task ListAllFeedKeys_UnionsStatusAndMonthRowsAcrossAssets()
    {
        await _index.UpsertFeedStatus(new FeedStatusIndexRow("binance", "A", "funding-rate", "", null, null, 0, "Healthy", "[]", "[]"), Ct);
        await _index.ReplaceMonths("binance", "B", "candles", "1h",
            [new MonthPartitionRow("2024-01", 10, 100, "2024-01-31T00:00:00Z")], Ct);
        var keys = await _index.ListAllFeedKeys(Ct);
        Assert.Contains(("binance", "A", "funding-rate", ""), keys);
        Assert.Contains(("binance", "B", "candles", "1h"), keys);
    }

    [Fact]
    public async Task Reads_AreCaseInsensitive_OnExchangeAndDir()
    {
        await _index.UpsertFeedStatus(new FeedStatusIndexRow("Binance", "BTCUSDT_Perp", "mark-price", "1h", null, null, 0, "Healthy", "[]", "[]"), Ct);
        await _index.ReplaceMonths("Binance", "BTCUSDT_Perp", "mark-price", "1h",
            [new MonthPartitionRow("2024-01", 10, 100, "2024-01-31T00:00:00Z")], Ct);
        Assert.Single(await _index.GetFeedStatuses("binance", "btcusdt_perp", Ct));
        Assert.Single(await _index.GetMonths("binance", "btcusdt_perp", "mark-price", "1h", Ct));
        Assert.Single(await _index.ListFeedKeys("binance", "btcusdt_perp", Ct));
    }

    [Fact]
    public async Task InstrumentMeta_BatchUpsert_AndFilteredList()
    {
        await _index.UpsertInstrumentMeta([
            new InstrumentMetaRow("binance", "BTCUSDT_perp", 1, 3, "0.10", "2026-07-11T00:00:00Z"),
            new InstrumentMetaRow("binance", "ETHUSDT", 2, 4, "0.01", "2026-07-11T00:00:00Z")], Ct);
        Assert.Equal(2, (await _index.ListInstrumentMeta("binance", Ct)).Count);

        // re-upsert overwrites in place (PK exchange+dir)
        await _index.UpsertInstrumentMeta([
            new InstrumentMetaRow("binance", "BTCUSDT_perp", 2, 3, "0.01", "2026-07-12T00:00:00Z")], Ct);
        var row = (await _index.ListInstrumentMeta("binance", Ct)).Single(r => r.Dir == "BTCUSDT_perp");
        Assert.Equal(2, row.PriceDecimals);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }
}
