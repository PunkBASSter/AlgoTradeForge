using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

internal sealed class IbDataPlaneOptions
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 4002;
    public int ClientId { get; init; } = 1;
    public int IngestChannelCapacity { get; init; } = 4096;
    public long MaxGapMs { get; init; } = 30_000;
    public Dictionary<string, TickScale> InstrumentScales { get; init; } = new();
    public TickScale DefaultScale { get; init; } = new(PriceExp: 2, QtyExp: 0);
}
