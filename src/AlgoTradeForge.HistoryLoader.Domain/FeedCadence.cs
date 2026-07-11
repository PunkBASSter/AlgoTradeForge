namespace AlgoTradeForge.HistoryLoader.Domain;

/// <summary>On-disk cadence interval per collected feed (file suffix YYYY-MM_{interval}.csv and
/// coverage math). Collector-owned, never group-declared: groups say WHAT to collect, this says
/// at what granularity the collectors write. "" = interval-less (monthly-completeness or stream
/// feeds). Values mirror the retired appsettings Assets[] feed intervals.</summary>
public static class FeedCadence
{
    public static string DiskInterval(string feedName) => feedName switch
    {
        FeedNames.MarkPrice or FeedNames.PremiumIndex or FeedNames.IndexPrice => "1h",
        FeedNames.OpenInterest => "5m",
        FeedNames.LsRatioGlobal or FeedNames.LsRatioTopAccounts or FeedNames.LsRatioTopPositions => "15m",
        FeedNames.TakerVolume => "15m",
        _ => "",   // candles carry explicit intervals; funding-rate/ticks/liquidations/book-ticker are interval-less
    };
}
