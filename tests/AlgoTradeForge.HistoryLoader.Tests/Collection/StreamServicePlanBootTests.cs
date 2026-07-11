using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Tests.TestData;
using AlgoTradeForge.HistoryLoader.WebApi.Collection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

// At boot ICollectionPlanSource.Current is CollectionPlan.Empty until DesiredStateService's
// first pipeline run publishes. Stream services must NOT exit permanently on an empty plan —
// they wait for PlanChanged and re-evaluate.
public sealed class StreamServicePlanBootTests
{
    private static IOptionsMonitor<HistoryLoaderOptions> Options()
    {
        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions
        {
            DataRoot = Path.GetTempPath(),
            // Unroutable local endpoint: any connect attempt fails fast, no real network I/O.
            Binance = new BinanceOptions
            {
                SpotWsBaseUrl = "ws://127.0.0.1:9",
                FuturesWsBaseUrl = "ws://127.0.0.1:9",
            },
        });
        return options;
    }

    private static ICollectionCircuitBreaker Breaker()
    {
        var breaker = Substitute.For<ICollectionCircuitBreaker>();
        breaker.IsTripped.Returns(false);
        return breaker;
    }

    [Fact]
    public async Task SpotAggTrade_EmptyPlanAtBoot_DoesNotExit()
    {
        var holder = new CollectionPlanHolder(); // Current == CollectionPlan.Empty

        var service = new SpotAggTradeStreamService(
            Substitute.For<ITickFeedWriter>(),
            Substitute.For<ISchemaManager>(),
            Substitute.For<IFeedStatusStore>(),
            Breaker(),
            Substitute.For<IHttpClientFactory>(),
            holder,
            Options(),
            NullLogger<SpotAggTradeStreamService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.False(service.ExecuteTask!.IsCompleted);

        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BookTicker_EmptyPlanAtBoot_DoesNotExit()
    {
        var holder = new CollectionPlanHolder();

        var service = new BookTickerStreamService(
            Substitute.For<IBookTickerWriter>(),
            Substitute.For<ISchemaManager>(),
            Substitute.For<IFeedStatusStore>(),
            Breaker(),
            Substitute.For<IHttpClientFactory>(),
            holder,
            Options(),
            NullLogger<BookTickerStreamService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.False(service.ExecuteTask!.IsCompleted);

        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SpotAggTrade_PlanPublishAfterBoot_ActivatesService()
    {
        var holder = new CollectionPlanHolder();
        var schemaManager = Substitute.For<ISchemaManager>();
        var ensured = new TaskCompletionSource();
        schemaManager
            .When(m => m.EnsureSchema(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string[]>(), Arg.Any<AutoApplySpec?>(), Arg.Any<CancellationToken>()))
            .Do(_ => ensured.TrySetResult());

        var service = new SpotAggTradeStreamService(
            Substitute.For<ITickFeedWriter>(),
            schemaManager,
            Substitute.For<IFeedStatusStore>(),
            Breaker(),
            Substitute.For<IHttpClientFactory>(),
            holder,
            Options(),
            NullLogger<SpotAggTradeStreamService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(service.ExecuteTask!.IsCompleted);

        holder.Publish(new CollectionPlan(
            [CollectionAssets.Spot("BTCUSDT", 2, CollectionAssets.Feed(FeedNames.Ticks, "", "eager"))],
            [], []));

        // Wait loop polls every 1s; EnsureSchemas fires before the (failing) connect attempt.
        var completed = await Task.WhenAny(ensured.Task, Task.Delay(5000, TestContext.Current.CancellationToken));
        Assert.Same(ensured.Task, completed);

        await service.StopAsync(TestContext.Current.CancellationToken);
    }
}
