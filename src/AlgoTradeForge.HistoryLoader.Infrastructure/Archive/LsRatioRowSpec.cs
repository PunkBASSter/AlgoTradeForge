namespace AlgoTradeForge.HistoryLoader.Infrastructure.Archive;

internal sealed class LsRatioRowSpec(int ratioCol) : MetricsRowSpec
{
    public override string[] Columns => ["long_pct", "short_pct", "ratio"];

    public override bool TryBuildRow(long ts, string[] row, out string csv)
    {
        csv = string.Empty;
        if (!TryValue(row, ratioCol, out var ratio))
            return false;
        var longPct = ratio / (1.0 + ratio);
        var shortPct = 1.0 / (1.0 + ratio);
        csv = $"{ts},{Format(longPct)},{Format(shortPct)},{Format(ratio)}";
        return true;
    }
}
