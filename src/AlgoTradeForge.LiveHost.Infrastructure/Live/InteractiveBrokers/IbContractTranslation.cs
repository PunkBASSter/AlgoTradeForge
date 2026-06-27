namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Translates the venue-neutral configured IbContract into the vendored IBApi wire type for reqContractDetails
// / placeOrder. Kept separate from the Asset<->IbContract mapper: this is the venue-DTO boundary (the IBApi
// reference stops here), mirroring BinanceAggTrade at the parser boundary. Futures are sent expiry-less so a
// single reqContractDetails returns every listed month.
internal static class IbContractTranslation
{
    public static IBApi.Contract ToIbApiContract(this IbContract spec) => new()
    {
        Symbol = spec.Symbol,
        SecType = spec.SecType.ToIbString(),
        Exchange = spec.Exchange,
        PrimaryExch = spec.PrimaryExch,
        Currency = spec.Currency,
    };

    // Market-data requests: reuse spec fields + stamp the runtime ConId so IB resolves unambiguously.
    public static IBApi.Contract ToIbApiContract(this ResolvedIbContract resolved)
    {
        var c = resolved.Spec.ToIbApiContract();
        c.ConId = resolved.ConId;
        return c;
    }
}
