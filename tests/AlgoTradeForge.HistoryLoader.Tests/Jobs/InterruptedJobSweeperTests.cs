using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;
using AlgoTradeForge.HistoryLoader.WebApi.Jobs;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Jobs;

public sealed class InterruptedJobSweeperTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("atf-interrupt-sweep-").FullName;
    private string _dataRoot = null!;
    private SqliteHistoryIndex _index = null!;
    private InterruptedJobSweeper _sweeper = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _dataRoot = Path.Combine(_dir, "data");
        Directory.CreateDirectory(_dataRoot);

        var init = new HistoryIndexInitializer(Path.Combine(_dir, "idx.sqlite"));
        await init.EnsureCreated(Ct);
        _index = new SqliteHistoryIndex(init, init.ConnectionString + ";Pooling=False");

        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = _dataRoot });
        _sweeper = new InterruptedJobSweeper(_index, options, NullLogger<InterruptedJobSweeper>.Instance);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private async Task<string> InterruptedJobTouching(string feedKey, string month)
    {
        var jobId = ((FeedGateOutcome.Acquired)await _index.TryAcquireFeedGate("load", feedKey, "{}", "{}", Ct)).JobId;
        await _index.SetTouched(jobId, feedKey, month, Ct);
        await _index.UpdateJob(jobId, "interrupted", ct: Ct);
        return jobId;
    }

    [Fact]
    public async Task Sweep_MissingFileForTouchedMonth_DeletesStaleRowAndOrphanTmp()
    {
        await InterruptedJobTouching("binance|BTCUSDT|candles|1m", "2024-03");

        // month_partitions has a stale row for 2024-03 but the CSV is absent; an orphan .tmp sits beside it.
        await _index.ReplaceMonths("binance", "BTCUSDT", "candles", "1m",
            [new MonthPartitionRow("2024-03", 100, 5000, DateTime.UtcNow.ToString("O"))], Ct);

        var feedDir = Path.Combine(_dataRoot, "binance", "BTCUSDT", "candles");
        Directory.CreateDirectory(feedDir);
        var orphanTmp = Path.Combine(feedDir, "2024-03_1m.csv.tmp-abc");
        await File.WriteAllTextAsync(orphanTmp, "partial", Ct);

        await _sweeper.SweepOnceForTest(Ct);

        Assert.Empty(await _index.GetMonths("binance", "BTCUSDT", "candles", "1m", Ct));
        Assert.False(File.Exists(orphanTmp));
    }

    [Fact]
    public async Task Sweep_PresentButIncompleteMonth_InvalidatesCompleteness()
    {
        const string feedKey = "binance|BTCUSDT_perp|funding-rate|";
        await InterruptedJobTouching(feedKey, "2024-03");

        // funding-rate coverage is the monthly-completeness signal on the feed_status row.
        await _index.UpsertFeedStatus(new FeedStatusIndexRow(
            "binance", "BTCUSDT_perp", "funding-rate", "",
            FirstTs: null, LastTs: null, RecordCount: 0,
            Health: "Healthy", GapsJson: "[]",
            CompleteMonthsJson: """["2024-02","2024-03"]"""), Ct);

        // The month's CSV IS present on disk (interval-less feed → {month}.csv).
        var feedDir = Path.Combine(_dataRoot, "binance", "BTCUSDT_perp", "funding-rate");
        Directory.CreateDirectory(feedDir);
        var presentFile = Path.Combine(feedDir, "2024-03.csv");
        await File.WriteAllTextAsync(presentFile, "ts,rate\n1,0.01\n", Ct);

        await _sweeper.SweepOnceForTest(Ct);

        // Completeness invalidated so convergence re-collects; present file left in place; sibling month kept.
        var status = Assert.Single(await _index.GetFeedStatuses("binance", "BTCUSDT_perp", Ct), s => s.FeedName == "funding-rate");
        var complete = JsonSerializer.Deserialize<string[]>(status.CompleteMonthsJson)!;
        Assert.DoesNotContain("2024-03", complete);
        Assert.Contains("2024-02", complete);
        Assert.True(File.Exists(presentFile));
    }

    [Fact]
    public async Task Sweep_NoInterruptedJobs_NoOp()
    {
        Assert.Equal(0, await _sweeper.SweepOnceForTest(Ct));
    }
}
