namespace AlgoTradeForge.HistoryLoader.Domain;

public static class FeedNames
{
    public const string Candles = "candles";
    public const string CandleExt = "candle-ext";
    public const string FundingRate = "funding-rate";
    public const string MarkPrice = "mark-price";
    public const string PremiumIndex = "premium-index";
    public const string IndexPrice = "index-price";
    public const string OpenInterest = "open-interest";
    public const string TakerVolume = "taker-volume";
    public const string LsRatioGlobal = "ls-ratio-global";
    public const string LsRatioTopAccounts = "ls-ratio-top-accounts";
    public const string LsRatioTopPositions = "ls-ratio-top-positions";
    public const string Liquidations = "liquidations";
    public const string Ticks = "ticks";
    public const string BookTicker = "book-ticker";
    public const string Session = "_session";

    // Interval-less, monthly-zip-sourced feeds: coverage is the CompleteMonths marker, not the
    // row-count predicate (they carry no interval string, so IntervalParser cannot run on them).
    public static bool UsesMonthlyCompleteness(string feedName) => feedName is Ticks or FundingRate;
}
