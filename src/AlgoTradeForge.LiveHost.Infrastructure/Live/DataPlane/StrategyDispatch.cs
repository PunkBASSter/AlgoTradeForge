using System.Collections.Concurrent;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane;

public sealed class StrategyDispatch(ILogger<StrategyDispatch> logger) : IStrategyDispatch
{
    private readonly ILogger<StrategyDispatch> _logger = logger;
    private readonly ConcurrentDictionary<Guid, SessionInterest> _sessions = new();

    public void Register(LiveSessionRegistration registration)
    {
        _sessions[registration.SessionId] = SessionInterest.Build(registration);
        _logger.LogDebug("StrategyDispatch registered session {SessionId}", registration.SessionId);
    }

    public void Unregister(Guid sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        _logger.LogDebug("StrategyDispatch unregistered session {SessionId}", sessionId);
    }

    public void DispatchBar(string instrument, BarSpecKey spec, in Int64Bar bar, bool isStart)
    {
        // `in` params cannot be captured in a closure — copy to a local first.
        var b = bar;

        // ConcurrentDictionary enumeration tolerates concurrent register/unregister.
        // Bars route unconditionally — the registered strategy is always IInt64BarStrategy.
        foreach (var session in _sessions.Values)
        {
            foreach (var bs in session.BarInterests)
            {
                if (bs.Instrument != instrument || !bs.Spec.Equals(spec)) continue;
                var strategy = session.Strategy;
                var sub = bs.Subscription;
                Action action = isStart
                    ? () => strategy.OnBarStart(b, sub)
                    : () => strategy.OnBarComplete(b, sub);
                session.DataWriter.TryWrite(action);
            }
        }
    }

    public void DispatchTick(string instrument, in TradeTick tick)
    {
        // `in` params cannot be captured in a closure — copy to a local first.
        var t = tick;

        foreach (var session in _sessions.Values)
        {
            if (session.TradeTickStrategy is not { } tts) continue;
            foreach (var ti in session.TickInterests)
            {
                if (ti.Instrument != instrument) continue;
                var sub = ti.Subscription;
                session.DataWriter.TryWrite(() => tts.OnTradeTick(in t, sub));
            }
        }
    }
}
