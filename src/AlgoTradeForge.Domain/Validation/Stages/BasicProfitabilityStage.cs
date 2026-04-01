namespace AlgoTradeForge.Domain.Validation.Stages;

/// <summary>
/// Stage 1: Basic profitability filter. Eliminates candidates that fail minimum
/// profit, profit factor, trade count, or drawdown thresholds.
/// </summary>
public sealed class BasicProfitabilityStage : IValidationStage
{
    public int StageNumber => 1;
    public string StageName => "BasicProfitability";

    public StageResult Execute(ValidationContext context, CancellationToken ct = default)
    {
        var thresholds = context.Profile.BasicProfitability;
        var survivors = new List<int>();
        var verdicts = new List<CandidateVerdict>(context.AllCandidateIndices.Count);

        foreach (var idx in context.AllCandidateIndices)
        {
            ct.ThrowIfCancellationRequested();

            var trial = context.Trials[idx];
            var m = trial.Metrics;
            var metrics = new Dictionary<string, double>
            {
                ["netProfit"] = (double)m.NetProfit,
                ["profitFactor"] = m.ProfitFactor,
                ["tradeCount"] = m.TotalTrades,
                ["maxDrawdownPct"] = m.MaxDrawdownPct,
            };

            string? reason = null;

            if (m.NetProfit <= thresholds.MinNetProfit)
                reason = "NET_PROFIT_NEGATIVE";
            else if (m.ProfitFactor < thresholds.MinProfitFactor)
                reason = "PROFIT_FACTOR_BELOW_THRESHOLD";
            else if (m.TotalTrades < thresholds.MinTradeCount)
                reason = "INSUFFICIENT_TRADES";
            else if (m.MaxDrawdownPct > thresholds.MaxDrawdownPct)
                reason = "EXCESSIVE_DRAWDOWN";

            // T-statistic: t = meanReturn * sqrt(n) / stdev
            var pnlDeltas = context.Cache.GetTrialPnl(idx);
            var barCount = pnlDeltas.Length;
            var tStat = 0.0;
            if (barCount >= 2)
            {
                var eq = (double)m.InitialCapital;
                var sumRet = 0.0;
                var sumRetSq = 0.0;
                for (var b = 0; b < barCount; b++)
                {
                    var ret = eq > 0 ? pnlDeltas[b] / eq : 0.0;
                    sumRet += ret;
                    sumRetSq += ret * ret;
                    eq += pnlDeltas[b];
                }

                var meanRet = sumRet / barCount;
                var variance = sumRetSq / barCount - meanRet * meanRet;
                if (variance > 0)
                    tStat = meanRet * Math.Sqrt(barCount) / Math.Sqrt(variance);
            }

            metrics["tStatistic"] = tStat;

            if (reason is null && tStat < thresholds.MinTStatistic)
                reason = "T_STATISTIC_BELOW_THRESHOLD";

            var passed = reason is null;
            verdicts.Add(new CandidateVerdict(trial.Id, passed, reason, metrics));
            if (passed) survivors.Add(idx);
        }

        return new StageResult(survivors, verdicts);
    }
}
