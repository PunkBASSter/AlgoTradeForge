using System.Reflection;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Strategy.Modules.Exit;
using AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;
using AlgoTradeForge.Domain.Strategy.Modules.Regime;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Strategy.Modules.TrailingStop;

namespace AlgoTradeForge.Domain.Strategy.Modules;

public class ModularStrategyParamsBase : StrategyParamsBase
{
    [Optimizable(Min = -50, Max = 50, Step = 10)]
    public int FilterThreshold { get; init; } = 0;

    [Optimizable(Min = 10, Max = 80, Step = 10)]
    public int SignalThreshold { get; init; } = 30;

    [Optimizable(Min = -100, Max = -20, Step = 10)]
    public int ExitThreshold { get; init; } = -50;

    [Optimizable(Min = 1.0, Max = 5.0, Step = 0.5)]
    public double DefaultAtrStopMultiplier { get; init; } = 2.0;

    public MoneyManagementParams MoneyManagement { get; init; } = new();
    public TradeRegistryParams TradeRegistry { get; init; } = new();
    public TrailingStopParams? TrailingStop { get; init; }
    public ExitParams? Exit { get; init; }
    public RegimeDetectorParams? RegimeDetector { get; init; }

    public Dictionary<string, int> FilterWeights { get; init; } = [];

    public int GetFilterWeight(IFilterModule filter)
    {
        var key = filter.GetType().GetCustomAttribute<ModuleKeyAttribute>()?.Key;
        return key is not null && FilterWeights.TryGetValue(key, out var w) ? w : 1;
    }
}
