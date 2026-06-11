namespace AlgoTradeForge.Domain.Strategy.Modules;

public interface IStrategyModule
{
    /// <summary>The configuration this module was constructed with, exposed so run
    /// records and templates can echo the effective config (a request that sends
    /// <c>{}</c> keeps the default module, which would otherwise be invisible).
    /// Modules without parameters return null.</summary>
    ModuleParamsBase? ModuleParams => null;
}

public interface IStrategyModule<TParams> : IStrategyModule
    where TParams : ModuleParamsBase;
