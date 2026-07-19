using System.Globalization;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Archive;

internal abstract class MetricsRowSpec
{
    public abstract string[] Columns { get; }

    // False when the source left a column this feed needs blank; the caller drops the row.
    public abstract bool TryBuildRow(long ts, string[] row, out string csv);

    public static MetricsRowSpec For(string feedName) => feedName switch
    {
        FeedNames.OpenInterest        => new OpenInterestRowSpec(),
        FeedNames.LsRatioGlobal       => new LsRatioRowSpec(ratioCol: 6),
        FeedNames.LsRatioTopAccounts  => new LsRatioRowSpec(ratioCol: 4),
        FeedNames.LsRatioTopPositions => new LsRatioRowSpec(ratioCol: 5),
        _ => throw new InvalidOperationException($"Unsupported metrics feed: {feedName}")
    };

    // Binance blanks metric columns on some rows, in two forms within the same dataset:
    // bare empty (BTCUSDT 2020-09) and quoted-empty (XRPUSDT 2021-12).
    protected static bool TryValue(string[] row, int col, out double value)
    {
        value = 0;
        if ((uint)col >= (uint)row.Length)
            return false;
        var raw = row[col].AsSpan().Trim().Trim('"');
        return !raw.IsEmpty
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    protected static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);
}
