using System.Reflection;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Modules;

namespace AlgoTradeForge.Application.Backtests;

/// <summary>
/// Builds the parameter dictionary echoed into run records and event-log metadata.
/// Starts from the raw request parameters and overwrites module-slot entries with
/// the effective module configuration (<c>{ typeKey, params }</c>), so a request
/// that sent <c>{}</c> (keep default) shows the module and values that actually ran.
/// </summary>
public static class EffectiveParamsEcho
{
    public static IDictionary<string, object> Build(
        IDictionary<string, object>? requestParams, IInt64BarStrategy strategy)
    {
        var echo = requestParams is null
            ? []
            : new Dictionary<string, object>(requestParams);

        if (strategy is not IStrategyParamsProvider provider)
            return echo;

        foreach (var prop in provider.StrategyParams.GetType()
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!typeof(IStrategyModule).IsAssignableFrom(prop.PropertyType))
                continue;

            if (prop.GetValue(provider.StrategyParams) is IStrategyModule module)
                echo[prop.Name] = ModuleDescriptor.Describe(module);
        }

        return echo;
    }
}
