using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;

namespace AlgoTradeForge.Application.Abstractions;

public interface IFeedContextBuilder
{
    BacktestFeedContext? Build(string dataRoot, Asset asset, DateOnly from, DateOnly to);
}
