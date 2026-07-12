using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Application.Jobs;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Tests.TestData;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using AlgoTradeForge.HistoryLoader.WebApi.Collection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

public sealed class ArchiveLoadServiceTests
{
    private static readonly CollectionAsset Asset = CollectionAssets.Perp("BTCUSDT");

    private readonly IBackfillOrchestrator _orchestrator = Substitute.For<IBackfillOrchestrator>();
    private readonly IOptionsMonitor<HistoryLoaderOptions> _options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
    private readonly IHistoryIndex _index = Substitute.For<IHistoryIndex>();
    private readonly NullLogger<ArchiveLoadService> _logger = NullLogger<ArchiveLoadService>.Instance;

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
        var svc = new ArchiveLoadService(_orchestrator, _options, _index, _logger);
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
        var svc = new ArchiveLoadService(_orchestrator, _options, _index, _logger);
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
                Arg.Any<IProgress<ArchiveProgress>?>(), Arg.Any<Func<string, CancellationToken, Task>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var svc = new ArchiveLoadService(_orchestrator, _options, _index, _logger);
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
                Arg.Any<IProgress<ArchiveProgress>?>(), Arg.Any<Func<string, CancellationToken, Task>?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                ci.Arg<IProgress<ArchiveProgress>?>()?.Report(new ArchiveProgress(1, 1, "2024-01"));
                return Task.FromResult(true);
            });

        var svc = new ArchiveLoadService(_orchestrator, _options, _index, _logger);
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
                Arg.Any<IProgress<ArchiveProgress>?>(), Arg.Any<Func<string, CancellationToken, Task>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var svc = new ArchiveLoadService(_orchestrator, _options, _index, _logger);
        var sink = new RecordingSink();
        var req = new ArchiveLoadRequest(Asset, FeedName: FeedNames.Candles, Interval: "1h", From: new(2024, 1, 1), To: new(2024, 1, 31));
        var ok = await svc.Run(req, sink, Ct);

        Assert.False(ok);
        Assert.Equal("symbol_busy", sink.FailCode);
    }

    // -------------------------------------------------------------------------
    // 5. Ordering — every progress Report must be durably written BEFORE the
    //    terminal Complete. Uses an async sink with a real yield point so a
    //    fire-and-forget bridge would let Complete overtake the pending Reports
    //    (tail mis-order → SSE never delivers; state regression complete→running).
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Run_ProgressReports_AllDrainBeforeComplete_InOrder()
    {
        _orchestrator.TryRunSingle(
                Arg.Any<CollectionAsset>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(),
                Arg.Any<IProgress<ArchiveProgress>?>(), Arg.Any<Func<string, CancellationToken, Task>?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var p = ci.Arg<IProgress<ArchiveProgress>?>();
                p?.Report(new ArchiveProgress(1, 3, "2024-01"));
                p?.Report(new ArchiveProgress(2, 3, "2024-02"));
                p?.Report(new ArchiveProgress(3, 3, "2024-03"));
                return Task.FromResult(true);
            });

        var svc = new ArchiveLoadService(_orchestrator, _options, _index, _logger);
        var sink = new OrderRecordingSink();
        var req = new ArchiveLoadRequest(Asset, FeedName: FeedNames.Candles, Interval: "1h", From: new(2024, 1, 1), To: new(2024, 3, 31));
        var ok = await svc.Run(req, sink, Ct);

        Assert.True(ok);
        var order = sink.Order;
        Assert.Equal(4, order.Count);
        Assert.Equal("complete", order[3]);
        for (var i = 0; i < 3; i++)
            Assert.Equal("progress", order[i]);
    }

    // -------------------------------------------------------------------------
    // 6. Progress-sink fault tolerance — if sink.Report throws a non-OCE
    //    exception the consumer must absorb it (best-effort progress) so the
    //    terminal Complete/Fail call ALWAYS runs and Run does NOT throw.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Run_ProgressReportThrows_StillReachesTerminal_DoesNotThrow()
    {
        _orchestrator.TryRunSingle(
                Arg.Any<CollectionAsset>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(),
                Arg.Any<IProgress<ArchiveProgress>?>(), Arg.Any<Func<string, CancellationToken, Task>?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                ci.Arg<IProgress<ArchiveProgress>?>()?.Report(new ArchiveProgress(1, 1, "2024-01"));
                return Task.FromResult(true);
            });

        var svc = new ArchiveLoadService(_orchestrator, _options, _index, _logger);
        var sink = new ThrowingProgressSink();
        var req = new ArchiveLoadRequest(Asset, FeedName: FeedNames.Candles, Interval: "1h", From: new(2024, 1, 1), To: new(2024, 1, 31));

        var ok = await svc.Run(req, sink, Ct);  // must not throw

        Assert.True(ok);
        Assert.True(sink.WasCompleted);
    }

    // Sink whose Report always throws to exercise the consumer's fault-absorption path.
    // Complete/Fail record normally so the terminal call can be observed.
    private sealed class ThrowingProgressSink : IJobProgressSink
    {
        public bool WasCompleted { get; private set; }
        public string? FailCode { get; private set; }

        public Task Report(string progressJson, CancellationToken ct = default) =>
            throw new InvalidOperationException("sqlite busy");

        public Task Started(string startedPayloadJson, CancellationToken ct = default) => Task.CompletedTask;

        public Task Complete(string resultPayloadJson, CancellationToken ct = default)
        {
            WasCompleted = true;
            return Task.CompletedTask;
        }

        public Task Fail(string code, string message, CancellationToken ct = default)
        {
            FailCode = code;
            return Task.CompletedTask;
        }

        public Task Cancel(string reason, CancellationToken ct = default) => Task.CompletedTask;
    }

    // Async sink with a real yield point in Report. Records the callback ORDER of
    // Report vs Complete so the ordering test can observe a race a synchronous
    // RecordingSink cannot. Lock-guarded so the fire-and-forget failure mode
    // surfaces as a clean assertion, not a concurrent-List crash.
    private sealed class OrderRecordingSink : IJobProgressSink
    {
        private readonly object _gate = new();
        private readonly List<string> _order = [];

        public IReadOnlyList<string> Order
        {
            get { lock (_gate) return [.._order]; }
        }

        public async Task Report(string progressJson, CancellationToken ct = default)
        {
            await Task.Delay(5, ct);
            lock (_gate) _order.Add("progress");
        }

        public Task Started(string startedPayloadJson, CancellationToken ct = default) => Task.CompletedTask;

        public Task Complete(string resultPayloadJson, CancellationToken ct = default)
        {
            lock (_gate) _order.Add("complete");
            return Task.CompletedTask;
        }

        public Task Fail(string code, string message, CancellationToken ct = default) => Task.CompletedTask;

        public Task Cancel(string reason, CancellationToken ct = default) => Task.CompletedTask;
    }
}
