using AlgoTradeForge.Domain.Aggregation;

namespace AlgoTradeForge.Domain.Aggregation.Accumulators;

// Equal-Volume accumulator. Emits a bar each time the base-volume sum reaches or exceeds
// threshold. Threshold and contribution are both in the source's quantity-scaled long units;
// SourceRecord.Volume arrives pre-scaled, so no MoneyConvert is needed at the sum site.
internal sealed class EqVAccumulator : AccumulatorBase
{
    public EqVAccumulator(long threshold) : base(threshold) { }

    protected override Int128 ThresholdContribution(in SourceRecord r) => r.Volume;
}
