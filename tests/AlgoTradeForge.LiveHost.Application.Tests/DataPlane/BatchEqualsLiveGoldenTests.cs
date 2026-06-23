using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Aggregation;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using Xunit;

namespace AlgoTradeForge.LiveHost.Application.Tests.DataPlane;

// Plan 4 acceptance test: the live alt-bar driver MUST emit bars byte-identical to the batch
// driver across every alt-bar family, because both construct via AccumulatorEntry.Open and feed
// SourceRecords through the same TryAdvance / TryDrainQueued contract.
//
// Batch-side form: DIRECT (TickToSourceRecord.From -> fresh AccumulatorEntry.Open -> TryAdvance
// + Renko drain), i.e. exactly what AggregationPipeline does AFTER PartitionedSourceReader hands
// it SourceRecords. This guards source-drain/emit-order equivalence on the engine by
// construction. It does NOT exercise the reader->record CSV step; that reader form was rejected
// because PartitionedSourceReader.ReadTickFile populates only Buy/SellVolumeLong from the tick
// CSV (never Buy/SellTradeCountLong) while it stands up DataFeedDescriptor + a temp CSV partition
// layout + IFileStorage — heavyweight for a unit test, and the trade-count fields it omits are
// irrelevant to AggregatedBar output anyway (the tick-path EqIT derives its +-1 from the signed
// Buy/SellVolumeLong, not the count fields).
public class BatchEqualsLiveGoldenTests
{
    private static ScaleContext Scale() => new(tickSize: 0.01m);

    [Theory]
    [InlineData("EqV")]
    [InlineData("EqT")]
    [InlineData("EqD")]
    [InlineData("EqIV")]
    [InlineData("EqID")]
    [InlineData("EqIT")]
    [InlineData("Range")]
    [InlineData("Renko")]
    public void Live_driver_emits_identical_bars_to_batch_driver(string typeCode)
    {
        var ticks = SyntheticTicks(count: 4000);
        var threshold = ThresholdFor(typeCode);
        var scale = Scale();

        var batch = RunBatch(typeCode, threshold, scale, ticks);

        var live = new List<Int64Bar>();
        var src = new TickAggregationBarSource(typeCode, threshold, scale, (bar, _) => live.Add(bar), recentCapacity: ticks.Count + 1);
        foreach (var t in ticks)
            src.Feed(in t);

        Assert.True(batch.Count > 0, $"{typeCode}: batch produced no bars — threshold too coarse, test would be vacuous.");
        Assert.Equal(batch, live); // record-struct equality is element-wise
    }

    // Batch driver's post-reader path: adapter -> accumulator -> primary emit + Renko drain.
    private static List<Int64Bar> RunBatch(string typeCode, long threshold, ScaleContext scale, List<TradeTick> ticks)
    {
        var acc = AccumulatorEntry.Open(typeCode, threshold, scale, scale, DataFeedKind.Tick);
        var bars = new List<Int64Bar>();
        foreach (var t in ticks)
        {
            var rec = TickToSourceRecord.From(in t);
            if (acc.TryAdvance(in rec, out var bar))
                bars.Add(ToInt64Bar(in bar));
            while (acc.TryDrainQueued(out var extra))
                bars.Add(ToInt64Bar(in extra));
        }
        return bars;
    }

    private static Int64Bar ToInt64Bar(in AggregatedBar b) =>
        new(b.TsMs, b.Open, b.High, b.Low, b.Close, b.Volume);

    // Thresholds are raw scaled longs picked so each family forms a healthy bar count over the
    // 4000-tick stream. Price-unit families (Range/Renko) are in price-ticks (tickSize 0.01 =>
    // ScaleFactor 100); base/dollar/count families are in their accumulator's native long unit.
    private static long ThresholdFor(string typeCode) => typeCode switch
    {
        "EqV" => 40,            // base-asset qty (qty is 1..20 per tick)
        "EqT" => 8,             // trade count
        "EqD" => 40_000_000,    // quote-asset dollar-ticks (qty * price * QuantityScale)
        "EqIV" => 20,           // signed base-asset qty imbalance
        "EqID" => 20_000_000,   // signed dollar-tick imbalance
        "EqIT" => 5,            // signed trade-count imbalance
        "Range" => 60,          // price-ticks (= $0.60 move); walk steps +-20 ticks/tick
        "Renko" => 50,           // brick size in price-ticks (= $0.50)
        _ => throw new ArgumentException($"No threshold mapped for '{typeCode}'.", nameof(typeCode)),
    };

    // Deterministic generator: LCG-driven price walk + qty + alternating-with-bias aggressor.
    // No unseeded randomness; fully reproducible across runs and machines.
    private static List<TradeTick> SyntheticTicks(int count)
    {
        var ticks = new List<TradeTick>(count);
        ulong state = 0x9E3779B97F4A7C15UL; // fixed seed
        long price = 5_000_000;             // $50,000.00 at tickSize 0.01
        long tsMs = 1_700_000_000_000;

        for (long i = 0; i < count; i++)
        {
            state = state * 6364136223846793005UL + 1442695040888963407UL;
            var r = (long)(state >> 33);

            var step = (r % 41) - 20;       // -20..+20 price-ticks
            price += step;
            if (price < 1_000_000) price = 1_000_000; // floor so it never goes non-positive

            var qty = (r % 20) + 1;         // 1..20 base-asset units (QuantityScale 1)
            tsMs += (r % 5) + 1;            // strictly increasing-ish; monotonic per loop

            // Bias toward Buy (~60%) so imbalance families accumulate a net signed magnitude and
            // actually cross their thresholds rather than oscillating around zero forever.
            var aggressor = (r % 5) < 3 ? AggressorSide.Buy : AggressorSide.Sell;

            ticks.Add(new TradeTick(tsMs, price, qty, i, aggressor));
        }

        return ticks;
    }
}
