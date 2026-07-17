using System.Globalization;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Collection;
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
    private readonly MetricsRowSpec _rowSpec = MetricsRowSpec.For(feedName);

    public string Exchange => "binance";
    public string FeedName => feedName;
    public bool Supports(string assetType) => AssetTypes.IsFutures(assetType);

    public async Task<ArchiveMonthResult> MaterializeMonth(
        CollectionAsset asset, CollectionFeed feed,
        string assetDir, int year, int month, CancellationToken ct = default)
    {
        var monthStart = new DateOnly(year, month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var rows = new List<string[]>();
        var available = false;

        // metrics is daily-only — no monthly zip exists for this dataset
        for (var day = monthStart; day <= monthEnd; day = day.AddDays(1))
        {
            await using var daily = await archive.DownloadDaily("futures/um", "metrics", asset.Venue.ApiSymbol, null, day, ct);
            if (daily is null) continue;
            using var reader = new StreamReader(daily);
            rows.AddRange(ArchiveCsv.ReadRows(reader));
            available = true;
        }

        if (!available)
            return new ArchiveMonthResult(0, AvailableAtSource: false);

        var fromMs = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(new DateOnly(year, month, 1).AddMonths(1), TimeOnly.MinValue, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var intervalMs = (long)IntervalParser.ToTimeSpan(feed.Interval).TotalMilliseconds;

        // create_time is a datetime string (UTC), not an epoch — parse accordingly
        var parsed = rows
            .Select(r => (Ts: ParseCreateTime(r[0]), Row: r))
            .Where(x => x.Ts >= fromMs && x.Ts < toMs && x.Ts % intervalMs == 0)
            .OrderBy(x => x.Ts)
            .ToList();

        // Dedup adjacent duplicate slots (Binance doubled 2020-09..2021-05) and drop rows the
        // source left blank — both BEFORE gap detection, so the holes become gaps instead of
        // aborting the month. parsed is sorted by Ts, so duplicates are adjacent.
        var built = new List<(long Ts, string Csv)>(parsed.Count);
        var lastTs = long.MinValue;
        long duplicates = 0;
        foreach (var (ts, row) in parsed)
        {
            if (ts == lastTs) { duplicates++; continue; }
            if (_rowSpec.TryBuildRow(ts, row, out var csv))
            {
                built.Add((ts, csv));
                lastTs = ts;
            }
        }
        var blanks = parsed.Count - built.Count - duplicates;

        if (duplicates > 0 || blanks > 0)
        {
            logger.LogWarning(
                "{Feed}/{Interval} {Year}-{Month:D2} {Symbol}: dropped {Dupes} duplicate + {Blanks} blank archive row(s) of {Total}",
                feedName, feed.Interval, year, month, asset.Venue.ApiSymbol,
                duplicates, blanks, parsed.Count);
        }

        if (built.Count == 0)
        {
            logger.LogWarning("metrics {Symbol} {Year}-{Month:D2}: archive present but 0 usable in-range rows",
                asset.Venue.ApiSymbol, year, month);
            return new ArchiveMonthResult(0, AvailableAtSource: true);
        }

        // Detect gaps from the actual downsampled row sequence (both ends are present rows)
        var gaps = ArchiveStatusMerger.DetectGaps(built.Select(x => x.Ts).ToList(), intervalMs);

        // Blank tail: the source shipped present-but-blank rows after the last usable one (dropped
        // above). Credit the observed blank region so the month reads covered instead of churning.
        // Bounded to maxParsedTs (last present row), NEVER month-end — else a blank-then-absent tail
        // would credit genuinely-missing days and falsely mark the month covered.
        var maxParsedTs = parsed[^1].Ts; // parsed is sorted ascending
        var lastUsableTs = built[^1].Ts;
        if (maxParsedTs > lastUsableTs)
            gaps.Add(new DataGap { FromMs = lastUsableTs, ToMs = maxParsedTs + intervalMs });

        var columns = _rowSpec.Columns;
        await schemaManager.EnsureSchema(assetDir, feedName, feed.Interval, columns, ct: ct);
        var path = Path.Combine(assetDir, feedName, $"{year:D4}-{month:D2}_{feed.Interval}.csv");
        var previousRows = await ArchiveStatusMerger.CountDataRows(path, ct);

        // Replace-guard: a sparse archive month must not clobber a fuller REST-collected one.
        if (built.Count < previousRows)
        {
            logger.LogWarning(
                "{Feed}/{Interval} {Year}-{Month:D2} {Symbol}: archive month has {New} rows < existing {Prev}; skipping replace",
                feedName, feed.Interval, year, month, asset.Venue.ApiSymbol, built.Count, previousRows);
            return new ArchiveMonthResult(0, AvailableAtSource: true);
        }

        await partitionWriter.ReplacePartition(
            path, $"ts,{string.Join(",", columns)}", built.Select(x => x.Csv), ct);

        await ArchiveStatusMerger.MergeStatus(
            feedStatusStore, assetDir, feedName, feed.Interval,
            built[0].Ts, built[^1].Ts, built.Count - previousRows, gaps, ct);

        logger.LogInformation("Materialized {Feed}/{Interval} {Year}-{Month:D2} for {Symbol}: {Rows} rows",
            feedName, feed.Interval, year, month, asset.Venue.ApiSymbol, built.Count);
        return new ArchiveMonthResult(built.Count, AvailableAtSource: true);
    }

    private static long ParseCreateTime(string value)
    {
        var dt = DateTime.ParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
    }

}
