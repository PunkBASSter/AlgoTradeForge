namespace AlgoTradeForge.HistoryLoader.Infrastructure.Archive;

internal sealed class OpenInterestRowSpec : MetricsRowSpec
{
    public override string[] Columns => ["oi", "oi_usd"];

    public override bool TryBuildRow(long ts, string[] row, out string csv)
    {
        csv = string.Empty;
        if (!TryValue(row, 2, out var oi) || !TryValue(row, 3, out var oiUsd))
            return false;
        csv = $"{ts},{Format(oi)},{Format(oiUsd)}";
        return true;
    }
}
