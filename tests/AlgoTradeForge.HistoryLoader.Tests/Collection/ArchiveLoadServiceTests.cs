using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Jobs;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Tests.TestData;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using AlgoTradeForge.HistoryLoader.WebApi.Collection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

public sealed class ArchiveLoadServiceTests
{
    private static readonly CollectionAsset Asset = CollectionAssets.Perp("BTCUSDT");

    private readonly IBackfillOrchestrator _orchestrator = Substitute.For<IBackfillOrchestrator>();
    private readonly IOptionsMonitor<HistoryLoaderOptions> _options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ArchiveLoadServiceTests()
    {
        _options.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = "/data/test" });
    }

    // -------------------------------------------------------------------------
    // 1. F1 guard — empty interval on an interval-based feed must not throw
    //    ArgumentException from IntervalParser; reports invalid_interval instead.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Run_IntervalBasedFeed_WithEmptyInterval_DoesNotThrow_ReportsInvalidInterval()
    {
        var svc = new ArchiveLoadService(_orchestrator, _options);
        var sink = new RecordingSink();
        var req = new ArchiveLoadRequest(Asset, FeedName: "candles", Interval: "", From: new(2024, 1, 1), To: new(2024, 1, 1));
        var ok = await svc.Run(req, sink, Ct);
        Assert.False(ok);
        Assert.Equal("invalid_interval", sink.FailCode);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("7x")]
    public async Task Run_IntervalBasedFeed_WithGarbageInterval_ReportsInvalidInterval(string interval)
    {
        var svc = new ArchiveLoadService(_orchestrator, _options);
        var sink = new RecordingSink();
        var req = new ArchiveLoadRequest(Asset, FeedName: FeedNames.Candles, Interval: interval, From: new(2024, 1, 1), To: new(2024, 1, 31));
        var ok = await svc.Run(req, sink, Ct);
        Assert.False(ok);
        Assert.Equal("invalid_interval", sink.FailCode);
    }

    // -------------------------------------------------------------------------
    // 2. Monthly-completeness feed (ticks/funding-rate) with empty interval
    //    must bypass the guard — no invalid_interval error.
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(FeedNames.Ticks)]
    [InlineData(FeedNames.FundingRate)]
    public async Task Run_MonthlyCompletenessFeed_EmptyInterval_DoesNotFailGuard(string feedName)
    {
        _orchestrator.TryRunSingle(
                Arg.Any<CollectionAsset>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(),
                Arg.Any<IProgress<ArchiveProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var svc = new ArchiveLoadService(_orchestrator, _options);
        var sink = new RecordingSink();
        var req = new ArchiveLoadRequest(Asset, FeedName: feedName, Interval: "", From: new(2024, 1, 1), To: new(2024, 1, 31));
        var ok = await svc.Run(req, sink, Ct);

        Assert.Null(sink.FailCode);
        Assert.True(ok);
    }

    // -------------------------------------------------------------------------
    // 3. Happy path — valid interval feed; orchestrator succeeds; progress flows.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Run_ValidIntervalFeed_OrchestratorSucceeds_StartsProgressCompletes()
    {
        _orchestrator.TryRunSingle(
                Arg.Any<CollectionAsset>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(),
                Arg.Any<IProgress<ArchiveProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                ci.Arg<IProgress<ArchiveProgress>?>()?.Report(new ArchiveProgress(1, 1, "2024-01"));
                return Task.FromResult(true);
            });

        var svc = new ArchiveLoadService(_orchestrator, _options);
        var sink = new RecordingSink();
        var req = new ArchiveLoadRequest(Asset, FeedName: FeedNames.Candles, Interval: "1h", From: new(2024, 1, 1), To: new(2024, 1, 31));
        var ok = await svc.Run(req, sink, Ct);

        Assert.True(ok);
        Assert.True(sink.WasStarted);
        Assert.NotEmpty(sink.Reports);
        Assert.True(sink.WasCompleted);
        Assert.Null(sink.FailCode);
    }

    // -------------------------------------------------------------------------
    // 4. Symbol busy — orchestrator returns false; Run returns false.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Run_OrchestratorReturnsFalse_ReturnsFalse_ReportsSymbolBusy()
    {
        _orchestrator.TryRunSingle(
                Arg.Any<CollectionAsset>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(),
                Arg.Any<IProgress<ArchiveProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var svc = new ArchiveLoadService(_orchestrator, _options);
        var sink = new RecordingSink();
        var req = new ArchiveLoadRequest(Asset, FeedName: FeedNames.Candles, Interval: "1h", From: new(2024, 1, 1), To: new(2024, 1, 31));
        var ok = await svc.Run(req, sink, Ct);

        Assert.False(ok);
        Assert.Equal("symbol_busy", sink.FailCode);
    }
}
