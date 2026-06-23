using AlgoTradeForge.Domain.Aggregation;

namespace AlgoTradeForge.Domain.Aggregation.Accumulators;

// Equal-Dollar (quote-volume) accumulator. Emits a bar each time the quote-volume sum reaches
// or exceeds threshold. Quote-volume is approximated per source record as Close × Volume
// (VWAP-style); threshold is in tick × quant units, resolved by the endpoint at job creation.
internal sealed class EqDAccumulator : AccumulatorBase
{
    public EqDAccumulator(long threshold) : base(threshold) { }

    // Int128 cast prevents long overflow on the per-record product (~10^14 on high-volume perps).
    protected override Int128 ThresholdContribution(in SourceRecord r) => (Int128)r.Close * r.Volume;
}
