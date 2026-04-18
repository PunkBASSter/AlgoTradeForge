namespace AlgoTradeForge.Domain.Strategy.Modules;

public interface IVolatilityContext
{
    long CurrentVolatility { get; set; }
}
