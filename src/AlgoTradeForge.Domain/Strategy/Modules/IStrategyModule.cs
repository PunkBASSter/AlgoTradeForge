namespace AlgoTradeForge.Domain.Strategy.Modules;

public interface IStrategyModule;

public interface IStrategyModule<TParams> : IStrategyModule
    where TParams : ModuleParamsBase;
