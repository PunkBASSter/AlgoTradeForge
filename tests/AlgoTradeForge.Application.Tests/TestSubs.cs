using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

// Test-only drop-in mirroring the retired flat DataSubscription(Asset, TimeFrame, FeedKey, IsExportable)
// record, producing a resolved DataFeedSubscription of the right kind. Null Asset is tolerated to
// match the old record (some tests pass null! when the asset is irrelevant to the callback).
internal static class TestSubs
{
    public static DataFeedSubscription Of(Asset Asset, TimeFrame TimeFrame, string FeedKey = "ohlcv", bool IsExportable = false)
    {
        var name = Asset?.Name ?? "";
        var exchange = Asset?.Exchange ?? "";
        DataFeedSubscription spec = FeedKey switch
        {
            "ohlcv" => new TimeBarSubscription(name, exchange, DataFeedRole.Primary, TimeFrame),
            "ticks" or "tick" => new TickSubscription(name, exchange, DataFeedRole.Primary),
            _ => new AltBarSubscription(name, exchange, DataFeedRole.Primary, FeedKey),
        };
        return spec with { Asset = Asset, IsExportable = IsExportable };
    }
}
