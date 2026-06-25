using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using Xunit;

namespace AlgoTradeForge.LiveHost.Application.Tests.Live.Recovery;

public class ReplayAbstractionsTests
{
    internal static Asset Btc() => CryptoPerpetualAsset.Create("BTCUSDT", "binance", decimalDigits: 2);

    [Fact]
    public async Task FakeReplaySource_yields_configured_ticks_in_order()
    {
        var ticks = new[] { Tick(10), Tick(11), Tick(12) };
        var src = new FakeReplaySource(ticks);
        var req = new ReplayRequest(Btc(), "binance", "ticks", FromTs: 0);

        var got = new List<long>();
        await foreach (var t in src.Replay(req, TestContext.Current.CancellationToken)) got.Add(t.Sequence);

        Assert.Equal(new long[] { 10, 11, 12 }, got);
    }

    internal static TradeTick Tick(long seq, long ts = 0) =>
        new(ts, Price: 100, Quantity: 1, Sequence: seq, Aggressor: AggressorSide.Buy);
}

internal sealed class FakeReplaySource(IReadOnlyList<TradeTick> ticks) : IReplaySource
{
    public async IAsyncEnumerable<TradeTick> Replay(ReplayRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken ct = default)
    {
        foreach (var t in ticks) { ct.ThrowIfCancellationRequested(); yield return t; await Task.Yield(); }
    }
}

internal sealed class FakeBackfillRequester(bool closes) : IBackfillRequester
{
    public int Calls { get; private set; }
    public Task<bool> TryBackfill(ReplayRequest context, Discontinuity gap, RecoveryPolicy policy, System.Threading.CancellationToken ct = default)
    { Calls++; return Task.FromResult(closes); }
}
