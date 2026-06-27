using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class IbBackfillRequesterTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"IbBf_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch { /* best-effort */ }
    }

    private sealed class FakeHist(IReadOnlyList<IbHistoricalTick> ticks) : IIbHistoricalTicksClient
    {
        public bool WasCalled { get; private set; }

        public Task<IReadOnlyList<IbHistoricalTick>> FetchTrades(
            ResolvedIbContract c, long fromMs, long toMs, CancellationToken ct)
        {
            WasCalled = true;
            return Task.FromResult(ticks);
        }
    }

    private static EquityAsset Aapl() => new() { Name = "AAPL", Exchange = "NASDAQ" };

    private static ResolvedIbContract AaplResolved() =>
        new(new IbContract("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD"), 265598, "AAPL", "");

    [Fact]
    public async Task TryBackfill_FetchesArchivesGap_AndReplaySourceReadsIt()
    {
        Directory.CreateDirectory(_root);
        var storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _root });
        var opts = new IbDataPlaneOptions { InstrumentScales = { ["AAPL"] = new TickScale(2, 0) } };
        var hist = new FakeHist([new IbHistoricalTick(1700, 296.50, 1m), new IbHistoricalTick(1701, 296.60, 2m)]);

        var resolver = Substitute.For<IIbContractResolver>();
        resolver.Resolve(Arg.Any<IbContract>(), Arg.Any<CancellationToken>())
            .Returns(AaplResolved());

        var aapl = Aapl();
        var assetResolver = Substitute.For<IIbInstrumentAssetResolver>();
        assetResolver.Resolve(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<Asset>(aapl));
        var sut = new IbBackfillRequester(hist, storage, "live-md", opts, resolver, assetResolver, new FakeTimeProvider());

        var req = new ReplayRequest(aapl, "ib", "ticks", FromTs: 0);
        var gap = new Discontinuity(FromTs: 1_700_000L, ToTs: 1_703_000L, DiscontinuityReason.MissingArchive);
        var policy = new RecoveryPolicy(BackfillBudget: TimeSpan.FromSeconds(5), PollInterval: TimeSpan.FromMilliseconds(50));

        var covered = await sut.TryBackfill(req, gap, policy, Ct);
        Assert.True(covered);

        var replay = new RelayArchiveReplaySource(storage, "live-md");
        var read = new List<TradeTick>();
        await foreach (var t in replay.Replay(req with { FromTs = 0 }, Ct))
            read.Add(t);

        Assert.Equal(2, read.Count);
        Assert.Equal(1_700_000L, read[0].TimestampMs);
        Assert.Equal(29_650L, read[0].Price);
        Assert.Equal(2L, read[1].Quantity);
    }

    [Fact]
    public async Task TryBackfill_ZeroBudget_ShortCircuitsFalse()
    {
        Directory.CreateDirectory(_root);
        var storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _root });
        var hist = new FakeHist([]);
        var resolver = Substitute.For<IIbContractResolver>();
        var aapl = Aapl();
        var assetResolver2 = Substitute.For<IIbInstrumentAssetResolver>();
        assetResolver2.Resolve(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<Asset>(aapl));

        var sut = new IbBackfillRequester(hist, storage, "live-md", new IbDataPlaneOptions(), resolver, assetResolver2, new FakeTimeProvider());

        var covered = await sut.TryBackfill(
            new ReplayRequest(aapl, "ib", "ticks", 0),
            new Discontinuity(1, 2, DiscontinuityReason.MissingArchive),
            new RecoveryPolicy(TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            Ct);

        Assert.False(covered);
        Assert.False(hist.WasCalled);
    }
}
