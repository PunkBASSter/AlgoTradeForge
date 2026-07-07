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
        var gaps = ArchiveStatusMerger.DetectGaps(parsed, intervalMs);

        var columns = GetColumns();
        await schemaManager.EnsureSchema(assetDir, feedName, feedConfig.Interval, columns, ct: ct);
        var path = Path.Combine(assetDir, feedName, $"{year:D4}-{month:D2}_{feedConfig.Interval}.csv");
        var previousRows = await ArchiveStatusMerger.CountDataRows(path, ct);

        // Replace-guard: a sparse archive month must not clobber a fuller REST-collected one.
        if (parsed.Count < previousRows)
        {
            logger.LogWarning(
                "{Feed}/{Interval} {Year}-{Month:D2} {Symbol}: archive month has {New} rows < existing {Prev}; skipping replace",
                feedName, feedConfig.Interval, year, month, assetConfig.Symbol, parsed.Count, previousRows);
            return new ArchiveMonthResult(0, AvailableAtSource: true);
        }

        var csvRows = parsed.Select(x => BuildRow(x.Ts, x.Row));
        await partitionWriter.ReplacePartition(path, $"ts,{string.Join(",", columns)}", csvRows, ct);

        await ArchiveStatusMerger.MergeStatus(
            feedStatusStore, assetDir, feedName, feedConfig.Interval,
            parsed[0].Ts, parsed[^1].Ts, parsed.Count - previousRows, gaps, ct);

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

}
