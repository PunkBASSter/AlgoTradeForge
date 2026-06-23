using System;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.LiveHost.Application.Live.DataPlane;

public interface IStrategyDispatch
{
    void Register(LiveSessionRegistration registration);
    void Unregister(Guid sessionId);
    void DispatchBar(string instrument, BarSpecKey spec, in Int64Bar bar, bool isStart);
    void DispatchTick(string instrument, in TradeTick tick);
}
