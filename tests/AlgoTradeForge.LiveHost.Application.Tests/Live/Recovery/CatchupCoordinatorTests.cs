using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using Xunit;
using static AlgoTradeForge.LiveHost.Application.Tests.Live.Recovery.ReplayAbstractionsTests;

namespace AlgoTradeForge.LiveHost.Application.Tests.Live.Recovery;

public class CatchupCoordinatorTests
{
    private static ReplayRequest Req() => new(Btc(), "binance", "ticks", FromTs: 0);

    [Fact]
    public async Task Contiguous_stream_passes_through_and_dedupes_overlap()
    {
        // Replay yields 10,11,11,12 (a duplicate); output must be 10,11,12.
        var src = new FakeReplaySource(new[] { Tick(10), Tick(11), Tick(11), Tick(12) });
        var coord = new CatchupCoordinator(src, new FakeBackfillRequester(closes: false), RecoveryPolicy.NoBackfill);
        var gate = new SequenceWatermarkGate();

        var got = new List<long>();
        await foreach (var t in coord.StreamFromBoundary(Req(), gate, _ => { }, TestContext.Current.CancellationToken)) got.Add(t.Sequence);

        Assert.Equal(new long[] { 10, 11, 12 }, got);
    }

    [Fact]
    public async Task Unbridgeable_gap_declares_discontinuity_and_resumes()
    {
        // Replay yields 10, then jumps to 20. Budget zero -> declare + resume at 20.
        var src = new FakeReplaySource(new[] { Tick(10, ts: 100), Tick(20, ts: 200) });
        var coord = new CatchupCoordinator(src, new FakeBackfillRequester(closes: false), RecoveryPolicy.NoBackfill);
        var gate = new SequenceWatermarkGate();

        var declared = new List<Discontinuity>();
        var got = new List<long>();
        await foreach (var t in coord.StreamFromBoundary(Req(), gate, declared.Add, TestContext.Current.CancellationToken)) got.Add(t.Sequence);

        Assert.Equal(new long[] { 10, 20 }, got);          // both delivered; 20 starts the new run
        var d = Assert.Single(declared);
        Assert.Equal(100, d.FromTs);                        // last-good tick (seq 10) ts
        Assert.Equal(200, d.ToTs);                          // first-after-gap tick (seq 20) ts
        Assert.Equal(DiscontinuityReason.MissingArchive, d.Reason);
    }

    [Fact]
    public async Task Multiple_distinct_gaps_are_each_backfilled_into_a_contiguous_stream()
    {
        // Seq 10, 50, 90 with gaps 10->50 and 50->90 (ts == seq here). The filling backfill closes each.
        var src = new MutableReplaySource(new[] { Tick(10, 10), Tick(50, 50), Tick(90, 90) });
        var backfill = new FillingBackfill(src);
        var coord = new CatchupCoordinator(src, backfill, new RecoveryPolicy(TimeSpan.FromSeconds(5), TimeSpan.Zero));
        var gate = new SequenceWatermarkGate();

        var declared = new List<Discontinuity>();
        var seqs = new List<long>();
        await foreach (var t in coord.StreamFromBoundary(Req(), gate, declared.Add, TestContext.Current.CancellationToken)) seqs.Add(t.Sequence);

        Assert.Empty(declared);                                            // both gaps bridged, none declared
        Assert.Equal(2, backfill.Calls);                                   // one attempt per distinct gap
        Assert.Equal(Enumerable.Range(10, 81).Select(i => (long)i), seqs); // 10..90 contiguous
    }

    [Fact]
    public async Task Backfill_reporting_success_without_closing_attempts_once_then_declares()
    {
        // Persistent gap 10->20 that backfill claims to close but never does (static source).
        var src = new FakeReplaySource(new[] { Tick(10, ts: 100), Tick(20, ts: 200) });
        var backfill = new FakeBackfillRequester(closes: true);
        var coord = new CatchupCoordinator(src, backfill, new RecoveryPolicy(TimeSpan.FromSeconds(5), TimeSpan.Zero));
        var gate = new SequenceWatermarkGate();

        var declared = new List<Discontinuity>();
        var seqs = new List<long>();
        await foreach (var t in coord.StreamFromBoundary(Req(), gate, declared.Add, TestContext.Current.CancellationToken)) seqs.Add(t.Sequence);

        Assert.Equal(1, backfill.Calls);                    // exactly one attempt — no infinite recursion
        var d = Assert.Single(declared);                    // then declared
        Assert.Equal(100, d.FromTs);
        Assert.Equal(200, d.ToTs);
        Assert.Equal(new long[] { 10, 20 }, seqs);
    }
}

// A replay source whose backing list can be mutated to simulate the archive gaining bridge records.
internal sealed class MutableReplaySource(IEnumerable<TradeTick> seed) : IReplaySource
{
    public List<TradeTick> Ticks { get; } = seed.ToList();

    public async IAsyncEnumerable<TradeTick> Replay(
        ReplayRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken ct = default)
    {
        foreach (var t in Ticks.OrderBy(x => x.Sequence).ToList()) // snapshot per call
        {
            if (t.TimestampMs >= request.FromTs) yield return t;
            await Task.Yield();
        }
    }
}

// Backfill that fills the missing contiguous sequences inside [gap] into the source (ts == seq here),
// then reports success — simulating a REST backfill that actually closes the gap.
internal sealed class FillingBackfill(MutableReplaySource src) : IBackfillRequester
{
    public int Calls { get; private set; }

    public Task<bool> TryBackfill(ReplayRequest context, Discontinuity gap, RecoveryPolicy policy, System.Threading.CancellationToken ct = default)
    {
        Calls++;
        for (var s = gap.FromTs + 1; s < gap.ToTs; s++)
            if (!src.Ticks.Any(t => t.Sequence == s)) src.Ticks.Add(Tick(s, s));
        return Task.FromResult(true);
    }
}
