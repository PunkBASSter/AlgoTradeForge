using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Tests.TestData;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

public sealed class BackfillOrchestratorTests
{
    private static SymbolCollector BuildCollector(IFeedCollector collector)
    {
        var archiveBackfill = new ArchiveBackfillService(
            new ArchiveMaterializerRegistry([]),                 // empty → CoverFromArchive is a no-op
            Substitute.For<IMonthCoverageCalculator>(),
            Substitute.For<IFeedStatusStore>(),
            Substitute.For<IHistoryIndex>(),
            new CollectionChangeNotifier(),
            TimeProvider.System,
            NullLogger<ArchiveBackfillService>.Instance);

        return new SymbolCollector(
            [collector],
            archiveBackfill,
            Substitute.For<IHistoryIndex>(),
            new CollectionChangeNotifier(),
            NullLogger<SymbolCollector>.Instance);
    }

    private static BackfillOrchestrator BuildOrchestrator(SymbolCollector collector)
    {
        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions
        {
            DataRoot = Path.GetTempPath(),
            MaxBackfillConcurrency = 2,
        });
        return new BackfillOrchestrator(collector, options, NullLogger<BackfillOrchestrator>.Instance);
    }

    // An HttpClient timeout inside a collector surfaces as TaskCanceledException (an OCE) whose token
    // is NOT the caller's ct. It must be swallowed per-asset, not let escape Task.WhenAll and abort the
    // remaining assets in the kick batch.
    [Fact]
    public async Task Run_TransientTimeoutFromCollector_DoesNotAbortBatch_AndCollectsOtherAssets()
    {
        var collector = Substitute.For<IFeedCollector>();
        collector.FeedName.Returns("open-interest");
        collector.SupportsSpot.Returns(true);

        // First asset's feed times out (OCE with no caller cancellation); second must still be collected.
        collector.Collect(
                Arg.Is<CollectionAsset>(a => a.Venue.ApiSymbol == "BTCUSDT"),
                Arg.Any<CollectionFeed>(), Arg.Any<string>(),
                Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new TaskCanceledException("simulated HttpClient timeout"));

        var orchestrator = BuildOrchestrator(BuildCollector(collector));

        var assets = new[]
        {
            CollectionAssets.Perp("BTCUSDT", 2, CollectionAssets.Feed("open-interest", "5m")),
            CollectionAssets.Perp("ETHUSDT", 2, CollectionAssets.Feed("open-interest", "5m")),
        };

        // Must NOT throw — pre-fix the timeout escaped Task.WhenAll and faulted Run.
        await orchestrator.Run(assets, ct: TestContext.Current.CancellationToken);

        await collector.Received().Collect(
            Arg.Is<CollectionAsset>(a => a.Venue.ApiSymbol == "ETHUSDT"),
            Arg.Any<CollectionFeed>(), Arg.Any<string>(),
            Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }
}
