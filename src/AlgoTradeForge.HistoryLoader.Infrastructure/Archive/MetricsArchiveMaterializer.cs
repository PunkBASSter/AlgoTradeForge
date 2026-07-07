using System.Globalization;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Archive;

internal sealed class MetricsArchiveMaterializer(
    string feedName,
    IBinanceArchiveClient archive,
    IPartitionFileWriter partitionWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<MetricsArchiveMaterializer> logger) : IArchiveMaterializer
{
    public string Exchange => "binance";
    public string FeedName => feedName;
    public bool Supports(string assetType) => AssetTypes.IsFutures(assetType);

    public async Task<ArchiveMonthResult> MaterializeMonth(
        AssetCollectionConfig assetConfig, FeedCollectionConfig feedConfig,
        string assetDir, int year, int month, CancellationToken ct = default)
    {
        var monthStart = new DateOnly(year, month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var rows = new List<string[]>();
        var available = false;

        // metrics is daily-only — no monthly zip exists for this dataset
        for (var day = monthStart; day <= monthEnd; day = day.AddDays(1))
        {
            await using var daily = await archive.DownloadDaily("futures/um", "metrics", assetConfig.Symbol, null, day, ct);
            if (daily is null) continue;
            using var reader = new StreamReader(daily);
            rows.AddRange(ArchiveCsv.ReadRows(reader));
            available = true;
        }

        if (!available)
            return new ArchiveMonthResult(0, AvailableAtSource: false);

        var fromMs = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(new DateOnly(year, month, 1).AddMonths(1), TimeOnly.MinValue, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var intervalMs = (long)IntervalParser.ToTimeSpan(feedConfig.Interval).TotalMilliseconds;

        // create_time is a datetime string (UTC), not an epoch — parse accordingly
        var parsed = rows
            .Select(r => (Ts: ParseCreateTime(r[0]), Row: r))
            .Where(x => x.Ts >= fromMs && x.Ts < toMs && x.Ts % intervalMs == 0)
            .OrderBy(x => x.Ts)
            .ToList();

        if (parsed.Count == 0)
        {
            logger.LogWarning("metrics {Symbol} {Year}-{Month:D2}: archive present but 0 in-range rows",
                assetConfig.Symbol, year, month);
            return new ArchiveMonthResult(0, AvailableAtSource: true);
        }

        // Detect gaps from the actual downsampled row sequence (both ends are present rows)
        var gaps = DetectGaps(parsed, intervalMs, feedConfig.GapThresholdMultiplier);

        var columns = GetColumns();
        await schemaManager.EnsureSchema(assetDir, feedName, feedConfig.Interval, columns, ct: ct);
        var path = Path.Combine(assetDir, feedName, $"{year:D4}-{month:D2}_{feedConfig.Interval}.csv");
        var csvRows = parsed.Select(x => BuildRow(x.Ts, x.Row));
        await partitionWriter.ReplacePartition(path, $"ts,{string.Join(",", columns)}", csvRows, ct);

        await MergeStatus(assetDir, feedConfig.Interval, parsed[0].Ts, parsed[^1].Ts, parsed.Count, gaps, ct);

        logger.LogInformation("Materialized {Feed}/{Interval} {Year}-{Month:D2} for {Symbol}: {Rows} rows",
            feedName, feedConfig.Interval, year, month, assetConfig.Symbol, parsed.Count);
        return new ArchiveMonthResult(parsed.Count, AvailableAtSource: true);
    }

    private static long ParseCreateTime(string value)
    {
        var dt = DateTime.ParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
    }

    private string[] GetColumns() => feedName switch
    {
        FeedNames.OpenInterest => ["oi", "oi_usd"],
        _ => ["long_pct", "short_pct", "ratio"]
    };

    private string BuildRow(long ts, string[] row) => feedName switch
    {
        FeedNames.OpenInterest      => BuildOiRow(ts, row),
        FeedNames.LsRatioGlobal     => BuildLsRow(ts, row, ratioCol: 6),
        FeedNames.LsRatioTopAccounts => BuildLsRow(ts, row, ratioCol: 4),
        FeedNames.LsRatioTopPositions => BuildLsRow(ts, row, ratioCol: 5),
        _ => throw new InvalidOperationException($"Unsupported metrics feed: {feedName}")
    };

    private static string BuildOiRow(long ts, string[] row)
    {
        var oi = double.Parse(row[2], CultureInfo.InvariantCulture);
        var oiUsd = double.Parse(row[3], CultureInfo.InvariantCulture);
        return $"{ts},{oi.ToString(CultureInfo.InvariantCulture)},{oiUsd.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string BuildLsRow(long ts, string[] row, int ratioCol)
    {
        var r = double.Parse(row[ratioCol], CultureInfo.InvariantCulture);
        var longPct = r / (1.0 + r);
        var shortPct = 1.0 / (1.0 + r);
        return $"{ts},{longPct.ToString(CultureInfo.InvariantCulture)},{shortPct.ToString(CultureInfo.InvariantCulture)},{r.ToString(CultureInfo.InvariantCulture)}";
    }

    // Mirrors FeedCollectorBase.DetectGap: FromMs and ToMs are both present rows
    private static List<DataGap> DetectGaps(List<(long Ts, string[] Row)> parsed, long intervalMs, double multiplier)
    {
        var gaps = new List<DataGap>();
        for (var i = 1; i < parsed.Count; i++)
        {
            var prev = parsed[i - 1].Ts;
            var curr = parsed[i].Ts;
            if (curr - prev > intervalMs * multiplier)
                gaps.Add(new DataGap { FromMs = prev, ToMs = curr });
        }
        return gaps;
    }

    private async Task MergeStatus(
        string assetDir, string interval,
        long monthFirst, long monthLast, long written,
        List<DataGap> newGaps, CancellationToken ct)
    {
        var existing = await feedStatusStore.Load(assetDir, feedName, interval, ct);

        var firstTs = existing?.FirstTimestamp.HasValue == true
            ? Math.Min(existing.FirstTimestamp.Value, monthFirst)
            : monthFirst;
        var lastTs = existing?.LastTimestamp.HasValue == true
            ? Math.Max(existing.LastTimestamp.Value, monthLast)
            : monthLast;
        var recordCount = (existing?.RecordCount ?? 0) + written;

        IReadOnlyList<DataGap> existingGaps = existing?.Gaps ?? [];
        var dedupedNew = newGaps
            .Where(g => !existingGaps.Any(e => e.FromMs == g.FromMs && e.ToMs == g.ToMs))
            .ToList();
        IReadOnlyList<DataGap> mergedGaps = [.. existingGaps, .. dedupedNew];
        var health = mergedGaps.Count == 0 ? CollectionHealth.Healthy : CollectionHealth.Degraded;

        await feedStatusStore.Save(assetDir, feedName, interval, new FeedStatus
        {
            FeedName = feedName,
            Interval = interval,
            FirstTimestamp = firstTs,
            LastTimestamp = lastTs,
            LastRunUtc = DateTimeOffset.UtcNow,
            RecordCount = recordCount,
            Gaps = mergedGaps,
            Health = health
        }, ct);
    }
}
