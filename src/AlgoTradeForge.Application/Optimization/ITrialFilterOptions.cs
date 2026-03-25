namespace AlgoTradeForge.Application.Optimization;

/// <summary>
/// Shared filter threshold properties for optimization commands.
/// Implemented by both brute-force and genetic command records.
/// </summary>
public interface ITrialFilterOptions
{
    double? MinProfitFactor { get; }
    double? MaxDrawdownPct { get; }
    double? MinSharpeRatio { get; }
    double? MinSortinoRatio { get; }
    double? MinAnnualizedReturnPct { get; }
}
