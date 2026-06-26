using AlgoTradeForge.Domain;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Polymorphic Asset <-> IbContract mapping: the GetSettlementCalculator(this Asset) idiom applied to
// instrument identity. Domain stays venue-neutral; all IB vocabulary lives here in the venue slice.
// Equity routes via SMART with a primary-listing exchange; futures route to their direct exchange with no
// primary and are resolved to a front-month conId later by the resolver.
internal static class IbContractMapping
{
    private const string SmartRouting = "SMART";
    private const string DefaultCurrency = "USD";

    public static IbContract ToIbContract(this Asset asset) => asset switch
    {
        EquityAsset => new IbContract(asset.Name, IbSecType.Stk, SmartRouting, asset.Exchange, DefaultCurrency),
        FutureAsset => new IbContract(asset.Name, IbSecType.Fut, asset.Exchange, PrimaryExch: "", DefaultCurrency),
        CryptoAsset => throw new NotSupportedException(
            "IB crypto routes via PAXOS and differs from a Binance-spot CryptoAsset — deferred to a later plan."),
        CryptoPerpetualAsset => throw new NotSupportedException(
            "Interactive Brokers has no crypto perpetual contracts."),
        _ => throw new ArgumentOutOfRangeException(
            nameof(asset), asset.GetType().Name, "Unsupported asset type for IB contract mapping."),
    };

    // Reverse map (for Plan 2/4 reconciliation of IB position/order pushback). Equity needs no enrichment;
    // futures reconstruction needs contractDetails multiplier/minTick (deferred — spec open point #3).
    public static Asset ToAsset(this ResolvedIbContract resolved) => resolved.Spec.SecType switch
    {
        IbSecType.Stk => new EquityAsset { Name = resolved.Spec.Symbol, Exchange = resolved.Spec.PrimaryExch },
        IbSecType.Fut => throw new NotSupportedException(
            "FutureAsset reconstruction needs contractDetails multiplier/minTick — deferred to Plan 2/4."),
        _ => throw new ArgumentOutOfRangeException(
            nameof(resolved), resolved.Spec.SecType, "Unsupported SecType for asset mapping."),
    };
}
