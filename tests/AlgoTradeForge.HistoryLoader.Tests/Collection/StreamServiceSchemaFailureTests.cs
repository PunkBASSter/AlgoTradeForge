using System.Runtime.CompilerServices;
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

// EnsureSchemas does a feeds.json read-modify-write that can throw a transient
// ConcurrencyConflictException (ETag) or Windows file-lock IOException. It runs on every
// resubscribe iteration, so it MUST be inside the reconnect try — an escaped exception faults
// the venue loop and, in the single-loop case, ExecuteAsync itself (default StopHost tears down
// the whole HistoryLoader host).
public sealed class StreamServiceSchemaFailureTests
{
    private static IOptionsMonitor<HistoryLoaderOptions> Options()
    {
        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions
        {
            DataRoot = Path.GetTempPath(),
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

    private static ISchemaManager ThrowingSchemaManager(StrongBox<int> calls)
    {
        var schemaManager = Substitute.For<ISchemaManager>();
        schemaManager
            .When(m => m.EnsureSchema(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string[]>(), Arg.Any<AutoApplySpec?>(), Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                Interlocked.Increment(ref calls.Value);
                throw new IOException("feeds.json is locked by another writer");
            });
        return schemaManager;
    }

    private static async Task WaitForCalls(StrongBox<int> calls, int target, CancellationToken ct)
    {
        var deadline = 0;
        while (Volatile.Read(ref calls.Value) < target && deadline++ < 250)
            await Task.Delay(20, ct);
        Assert.True(Volatile.Read(ref calls.Value) >= target,
            $"expected >= {target} EnsureSchema attempts, saw {Volatile.Read(ref calls.Value)}");
    }

    [Fact]
    public async Task SpotAggTrade_EnsureSchemasThrows_DoesNotFaultHost()
    {
        var holder = new CollectionPlanHolder();
        var calls = new StrongBox<int>(0);

        var service = new SpotAggTradeStreamService(
            Substitute.For<ITickFeedWriter>(),
            ThrowingSchemaManager(calls),
            Substitute.For<IFeedStatusStore>(),
            Breaker(),
            Substitute.For<IHttpClientFactory>(),
            holder,
            Options(),
            NullLogger<SpotAggTradeStreamService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        holder.Publish(new CollectionPlan(
            [CollectionAssets.Spot("BTCUSDT", 2, CollectionAssets.Feed(FeedNames.Ticks, "", "eager"))],
            [], []));

        await WaitForCalls(calls, 1, TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.False(service.ExecuteTask!.IsFaulted);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BookTicker_EnsureSchemasThrows_DoesNotFaultHost()
    {
        var holder = new CollectionPlanHolder();
        var calls = new StrongBox<int>(0);

        var service = new BookTickerStreamService(
            Substitute.For<IBookTickerWriter>(),
            ThrowingSchemaManager(calls),
            Substitute.For<IFeedStatusStore>(),
            Breaker(),
            Substitute.For<IHttpClientFactory>(),
            holder,
            Options(),
            NullLogger<BookTickerStreamService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        // Both venues eligible: each loop's EnsureSchemas throws, so both venue tasks fault and
        // Task.WhenAll faults ExecuteAsync — visible as ExecuteTask.IsFaulted (a spot-only plan
        // would leave the futures loop running, masking the fault).
        holder.Publish(new CollectionPlan(
            [
                CollectionAssets.Spot("BTCUSDT", 2, CollectionAssets.Feed(FeedNames.BookTicker, "", "eager")),
                CollectionAssets.Perp("ETHUSDT", 2, CollectionAssets.Feed(FeedNames.BookTicker, "", "eager")),
            ],
            [], []));

        await WaitForCalls(calls, 2, TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.False(service.ExecuteTask!.IsFaulted);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }
}
