using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Live.Relay;

// Best-effort fan-out hook: archival writes the trade first (lossless), then taps observe it.
// Lives in Live.Relay so the lib needs no reference to LiveHost.Application (where ITickRouter lives).
public interface IRelayTradeTap
{
    void OnTrade(string instrument, in TradeTick tick);
}
