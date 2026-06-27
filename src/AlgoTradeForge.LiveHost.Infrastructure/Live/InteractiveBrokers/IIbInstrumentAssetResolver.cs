using AlgoTradeForge.Domain;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

internal interface IIbInstrumentAssetResolver
{
    ValueTask<Asset> Resolve(string instrument, CancellationToken ct = default);
}
