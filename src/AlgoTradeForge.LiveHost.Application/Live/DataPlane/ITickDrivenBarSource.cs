using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.LiveHost.Application.Live.DataPlane;

public interface ITickDrivenBarSource : IBarSource
{
    void Feed(in TradeTick tick);
}
