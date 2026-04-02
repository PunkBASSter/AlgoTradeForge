using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy.Modules.TrailingStop;

public record struct TrailingStopState
{
    public long CurrentStop { get; set; }
    public OrderSide Direction { get; init; }
    public long ActivationPrice { get; init; }
    public long HighWaterMark { get; set; }
}
