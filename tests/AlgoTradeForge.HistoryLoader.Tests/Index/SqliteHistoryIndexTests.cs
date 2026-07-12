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
        var queued = await _index.GetJob(id, Ct);
        Assert.Equal("queued", queued!.State);
        Assert.Null(await _index.GetActiveJob("rebuild", Ct));  // not running yet

        await _index.UpdateJob(id, "running", ct: Ct);
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
    public async Task JobEvents_Append_ReturnsMonotonicSeq_AndReadsAfter()
    {
        var jobId = await _index.CreateJob("aggregation", Ct);
        Assert.Equal(1, await _index.AppendJobEvent(jobId, "started", "{}", Ct));
        Assert.Equal(2, await _index.AppendJobEvent(jobId, "progress", """{"done":1}""", Ct));
        Assert.Equal(3, await _index.AppendJobEvent(jobId, "progress", """{"done":2}""", Ct));

        var after1 = await _index.GetJobEventsAfter(jobId, 1, Ct);
        Assert.Equal(new[] { 2, 3 }, after1.Select(e => e.Seq));
        Assert.Equal("progress", after1[0].Kind);
        Assert.Equal(3, await _index.GetLastEventSeq(jobId, Ct));
        Assert.Empty(await _index.GetJobEventsAfter(jobId, 3, Ct));
    }

    [Fact]
    public async Task AppendJobEvent_ConcurrentAppends_MonotonicSeq_NoBusy()
    {
        var jobId = await _index.CreateJob("load", Ct);
        var tasks = Enumerable.Range(0, 50)
            .Select(i => _index.AppendJobEvent(jobId, "progress", $$"""{"i":{{i}}}""", Ct));
        var seqs = await Task.WhenAll(tasks);   // must not throw SqliteException(SQLITE_BUSY)
        Assert.Equal(Enumerable.Range(1, 50), seqs.OrderBy(s => s));   // 1..50, all distinct
    }

    [Fact]
    public async Task TryAcquireFeedGate_ConcurrentSameFeed_ExactlyOneAcquires()
    {
        const string fk = "binance|BTCUSDT_perp|candles|1m";
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => _index.TryAcquireFeedGate("load", fk, "{}", "{}", Ct)));

        Assert.Single(outcomes, o => o is FeedGateOutcome.Acquired);
        Assert.Equal(19, outcomes.Count(o => o is FeedGateOutcome.Busy));
        var owner = outcomes.OfType<FeedGateOutcome.Acquired>().Single().JobId;
        Assert.All(outcomes.OfType<FeedGateOutcome.Busy>(), b => Assert.Equal(owner, b.ExistingJobId));

        // A different feed_key is not blocked.
        Assert.IsType<FeedGateOutcome.Acquired>(
            await _index.TryAcquireFeedGate("load", "binance|ETHUSDT|candles|1m", "{}", "{}", Ct));

        // Terminal state releases the gate — a new claim on fk now succeeds.
        await _index.UpdateJob(owner, "complete", ct: Ct);
        Assert.IsType<FeedGateOutcome.Acquired>(await _index.TryAcquireFeedGate("load", fk, "{}", "{}", Ct));
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

    [Fact]
    public async Task Cancel_Touched_List_Retention_RoundTrip()
    {
        var g = await _index.TryAcquireFeedGate("load", "binance|BTCUSDT|candles|1m", "{}", "{}", Ct);
        var id = Assert.IsType<FeedGateOutcome.Acquired>(g).JobId;
        var g2 = await _index.TryAcquireFeedGate("load", "binance|ETHUSDT|candles|1m", "{}", "{}", Ct);
        var otherId = Assert.IsType<FeedGateOutcome.Acquired>(g2).JobId;

        await _index.SetTouched(id, "binance|BTCUSDT|candles|1m", "2024-03", Ct);
        await _index.RequestCancel(id, Ct);
        var row = await _index.GetJob(id, Ct);
        Assert.True(row!.CancelRequested);
        Assert.Contains("2024-03", row.TouchedJson);

        await _index.UpdateJob(id, "running", ct: Ct);
        Assert.Single(await _index.ListJobs("load", "running", Ct));

        // Mark interrupted → appears in ListInterruptedJobs with touched.
        await _index.UpdateJob(id, "interrupted", ct: Ct);
        var interrupted = await _index.ListInterruptedJobs(Ct);
        Assert.Equal(id, interrupted.Single().Id);
        Assert.Contains("2024-03", interrupted.Single().TouchedJson);

        // Retention: a terminal job with an old updated_at is deleted with its events.
        await _index.AppendJobEvent(id, "progress", "{}", Ct);
        await _index.UpdateJob(id, "complete", ct: Ct);
        var deleted = await _index.DeleteTerminalJobsBefore(DateTimeOffset.UtcNow.AddMinutes(1), Ct);
        Assert.Equal(1, deleted);
        Assert.Null(await _index.GetJob(id, Ct));
        Assert.Empty(await _index.GetJobEventsAfter(id, 0, Ct));

        // DeleteJob removes row and its events.
        await _index.DeleteJob(otherId, Ct);
        Assert.Null(await _index.GetJob(otherId, Ct));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }
}
