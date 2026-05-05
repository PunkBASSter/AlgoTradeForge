namespace AlgoTradeForge.HistoryLoader.Application.Aggregation.Accumulators;

/// <summary>
/// Equal-Tick accumulator (TRD §6.3). Emits a bar every <c>threshold</c> source records.
/// Each source record contributes <c>1</c> to the threshold accumulator regardless of its
/// volume. The bar's <see cref="AggregatedBar.Volume"/> still reports the summed base volume
/// (so the on-disk schema stays uniform across alt-bar types).
/// </summary>
internal sealed class EqTAccumulator : AccumulatorBase
{
    public EqTAccumulator(long threshold) : base(threshold) { }

    protected override Int128 ThresholdContribution(in SourceRecord r) => Int128.One;
}
