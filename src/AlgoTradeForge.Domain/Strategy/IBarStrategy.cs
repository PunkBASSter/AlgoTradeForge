namespace AlgoTradeForge.Domain.Strategy;

public interface IBarStrategy
{
    StrategyAction? OnBar(StrategyContext context);
}
