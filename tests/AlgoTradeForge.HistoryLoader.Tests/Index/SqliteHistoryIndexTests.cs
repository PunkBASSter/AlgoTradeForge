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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }
}
