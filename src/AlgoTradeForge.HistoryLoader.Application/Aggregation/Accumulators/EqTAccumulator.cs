namespace AlgoTradeForge.HistoryLoader.Application.Aggregation.Accumulators;

// Equal-Tick accumulator. Emits a bar every `threshold` source records. The bar's Volume still
// reports the summed base volume so the on-disk schema is uniform across alt-bar types.
internal sealed class EqTAccumulator : AccumulatorBase
{
    public EqTAccumulator(long threshold) : base(threshold) { }

    protected override Int128 ThresholdContribution(in SourceRecord r) => Int128.One;
}
