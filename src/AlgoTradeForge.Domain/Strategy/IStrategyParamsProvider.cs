namespace AlgoTradeForge.Domain.Strategy;

/// <summary>
/// Exposes the bound parameter object of a strategy instance so run records can
/// echo the effective configuration (defaults included) rather than the raw request.
/// </summary>
public interface IStrategyParamsProvider
{
    StrategyParamsBase StrategyParams { get; }
}
