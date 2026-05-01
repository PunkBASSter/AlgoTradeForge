namespace AlgoTradeForge.HistoryLoader.Application.Aggregation.Accumulators;

/// <summary>
/// Equal-Dollar (quote-volume) accumulator (TRD §6.3). Emits a bar each time the
/// quote-volume sum reaches or exceeds <c>threshold</c>.
/// </summary>
/// <remarks>
/// In Phase 1b without a <c>candle-ext</c> join, quote-volume is approximated per source
/// record as <c>Close × Volume</c> — VWAP-style. The dimensional units of the threshold are
/// therefore <c>tick × quant</c> (the product of price-tick and quantity scales); the
/// endpoint resolves the user-facing decimal threshold into those units once at job creation.
///
/// When Phase 2b adds the <c>candle-ext</c> join + <c>quote_vol</c> column, the source reader
/// can emit a <c>SourceRecord</c> variant carrying pre-computed quote-volume and this
/// accumulator's contribution becomes a direct read. The Int64-money sum-site conversion
/// (P1b-8 / TRD §3.6) is the natural home for that <c>double → long</c> step.
/// </remarks>
internal sealed class EqDAccumulator : AccumulatorBase
{
    public EqDAccumulator(long threshold) : base(threshold) { }

    protected override long ThresholdContribution(in SourceRecord r) => r.Close * r.Volume;
}
