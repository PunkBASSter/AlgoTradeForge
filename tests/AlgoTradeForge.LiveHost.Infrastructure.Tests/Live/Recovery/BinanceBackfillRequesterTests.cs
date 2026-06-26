using AlgoTradeForge.Domain;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.Recovery;

public class BinanceBackfillRequesterTests
{
    private static Asset Btc() => CryptoPerpetualAsset.Create("BTCUSDT", "binance", decimalDigits: 2);
    private static ReplayRequest Req() => new(Btc(), "binance", "ticks", 0);
    private static Discontinuity Gap() => new(1000, 2000, DiscontinuityReason.MissingArchive);

    [Fact]
    public async Task Returns_true_when_client_archives_the_gap()
    {
        var stub = new CountingStubClient(succeeds: true);
        var req = new BinanceBackfillRequester(stub, new FakeTimeProvider());
        Assert.True(await req.TryBackfill(Req(), Gap(), new RecoveryPolicy(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1)), TestContext.Current.CancellationToken));
        Assert.True(stub.CallCount >= 1);
    }

    [Fact]
    public async Task Returns_false_when_budget_is_zero()
    {
        var stub = new CountingStubClient(succeeds: false);
        var req = new BinanceBackfillRequester(stub, new FakeTimeProvider());
        Assert.False(await req.TryBackfill(Req(), Gap(), RecoveryPolicy.NoBackfill, TestContext.Current.CancellationToken));
        Assert.Equal(0, stub.CallCount);
    }

    [Fact]
    public async Task Returns_false_after_polling_when_budget_expires()
    {
        var fakeTime = new FakeTimeProvider();
        var policy = new RecoveryPolicy(BackfillBudget: TimeSpan.FromSeconds(10), PollInterval: TimeSpan.FromSeconds(2));
        var stub = new CountingStubClient(succeeds: false);
        var requester = new BinanceBackfillRequester(stub, fakeTime);

        var task = requester.TryBackfill(Req(), Gap(), policy, TestContext.Current.CancellationToken);

        // Pump the fake clock until the awaited Task.Delay loop reaches the deadline and the task completes.
        // Advance one poll interval at a time; yield so the ConfigureAwait(false) continuation runs on the pool.
        var safety = 0;
        while (!task.IsCompleted && safety++ < 100)
        {
            fakeTime.Advance(policy.PollInterval);
            await Task.Yield();
        }

        Assert.True(task.IsCompleted, "TryBackfill did not complete after advancing past the budget");
        Assert.False(await task);
        Assert.True(stub.CallCount >= 1, "client should have been polled at least once");
    }

    private sealed class CountingStubClient(bool succeeds) : IAggTradeBackfillClient
    {
        public int CallCount { get; private set; }
        public Task<bool> FetchAndArchive(string instrument, long fromTs, long toTs, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(succeeds);
        }
    }
}
