namespace AlgoTradeForge.HistoryLoader.Application.Aggregation.Accumulators;

/// <summary>
/// Equal-Volume accumulator (TRD §6.3). Emits a bar each time the base-volume sum reaches
/// or exceeds <c>threshold</c>. Threshold and contribution are both in the source's
/// quantity-scaled <c>long</c> units (no <c>MoneyConvert</c> needed at the sum site —
/// <see cref="SourceRecord.Volume"/> arrives pre-scaled from the source reader).
/// </summary>
internal sealed class EqVAccumulator : AccumulatorBase
{
    public EqVAccumulator(long threshold) : base(threshold) { }

    protected override Int128 ThresholdContribution(in SourceRecord r) => r.Volume;
}
