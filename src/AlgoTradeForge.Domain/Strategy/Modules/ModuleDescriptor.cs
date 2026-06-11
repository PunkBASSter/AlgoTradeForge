using System.Reflection;
using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.Modules;

/// <summary>
/// Renders a module instance as the same <c>{ typeKey, params }</c> shape the API
/// accepts for module selection, so echoed run parameters and templates show the
/// effective configuration and are directly resubmittable.
/// </summary>
public static class ModuleDescriptor
{
    public static Dictionary<string, object?> Describe(IStrategyModule module)
    {
        var result = new Dictionary<string, object?>
        {
            ["typeKey"] = module.GetType().GetCustomAttribute<ModuleKeyAttribute>()?.Key
                ?? module.GetType().Name,
        };

        if (module.ModuleParams is { } moduleParams)
            result["params"] = moduleParams;

        return result;
    }
}
