using AlgoTradeForge.Domain.Reporting;

namespace AlgoTradeForge.Application.Optimization;

public sealed class TrialFilter
{
    private readonly int? _minTradeCount;
    private readonly decimal? _minNetProfit;
    private readonly double? _minProfitFactor;
    private readonly double? _maxDrawdownPct;
    private readonly double? _minSharpeRatio;
    private readonly double? _minSortinoRatio;
    private readonly double? _minAnnualizedReturnPct;

    public TrialFilter(int? minTradeCount, decimal? minNetProfit,
        double? minProfitFactor, double? maxDrawdownPct,
        double? minSharpeRatio, double? minSortinoRatio, double? minAnnualizedReturnPct)
    {
        _minTradeCount = minTradeCount;
        _minNetProfit = minNetProfit;
        _minProfitFactor = minProfitFactor;
        _maxDrawdownPct = maxDrawdownPct;
        _minSharpeRatio = minSharpeRatio;
        _minSortinoRatio = minSortinoRatio;
        _minAnnualizedReturnPct = minAnnualizedReturnPct;
    }

    public TrialFilter(ITrialFilterOptions options)
        : this(options.MinTradeCount, options.MinNetProfit,
            options.MinProfitFactor, options.MaxDrawdownPct, options.MinSharpeRatio,
            options.MinSortinoRatio, options.MinAnnualizedReturnPct) { }

    public bool Passes(PerformanceMetrics m) =>
        (!_minTradeCount.HasValue          || m.TotalTrades         >= _minTradeCount.Value) &&
        (!_minNetProfit.HasValue           || m.NetProfit           >= _minNetProfit.Value) &&
        (!_minProfitFactor.HasValue        || m.ProfitFactor        >= _minProfitFactor.Value) &&
        (!_maxDrawdownPct.HasValue         || m.MaxDrawdownPct      <= _maxDrawdownPct.Value) &&
        (!_minSharpeRatio.HasValue         || m.SharpeRatio         >= _minSharpeRatio.Value) &&
        (!_minSortinoRatio.HasValue        || m.SortinoRatio        >= _minSortinoRatio.Value) &&
        (!_minAnnualizedReturnPct.HasValue || m.AnnualizedReturnPct >= _minAnnualizedReturnPct.Value);
}
