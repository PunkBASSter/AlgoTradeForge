using System.Globalization;
using AlgoTradeForge.Domain;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Archive;

internal sealed class KlinesArchiveMaterializer(
    string feedName,
    string dataset,
    bool supportsSpot,
    IBinanceArchiveClient archive,
    IPartitionFileWriter partitionWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<KlinesArchiveMaterializer> logger) : IArchiveMaterializer
{
    private static readonly string[] ExtColumns =
        ["quote_vol", "trade_count", "taker_buy_vol", "taker_buy_quote_vol", "taker_buy_trade_count"];

    public string Exchange => "binance";
    public string FeedName => feedName;
    public bool Supports(string assetType) =>
        (supportsSpot && AssetTypes.IsSpot(assetType)) || AssetTypes.IsFutures(assetType);

    public async Task<ArchiveMonthResult> MaterializeMonth(
        CollectionAsset asset, CollectionFeed feed,
        string assetDir, int year, int month, CancellationToken ct = default)
    {
        var market = AssetTypes.IsSpot(asset.Venue.AssetType) ? "spot" : "futures/um";
        var interval = feed.Interval;
        var rows = new List<string[]>();
        var available = false;

        await using (var monthly = await archive.DownloadMonthly(market, dataset, asset.Venue.ApiSymbol, interval, year, month, ct))
        {
            if (monthly is not null)
            {
                using var reader = new StreamReader(monthly);
                rows.AddRange(ArchiveCsv.ReadRows(reader));
                available = true;
            }
        }

        if (!available)
        {
            // Closed months only (ownership rule) — no "clamp to today" needed; the caller
            // never passes the current month. TODO: parallelize daily downloads within
            // ArchiveDownloadConcurrency if 31 sequential round-trips prove slow.
            var monthStart = new DateOnly(year, month, 1);
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);

            for (var day = monthStart; day <= monthEnd; day = day.AddDays(1))
            {
                await using var daily = await archive.DownloadDaily(market, dataset, asset.Venue.ApiSymbol, interval, day, ct);
                if (daily is null) continue;
                using var reader = new StreamReader(daily);
                rows.AddRange(ArchiveCsv.ReadRows(reader));
                available = true;
            }
        }

        if (!available)
            return new ArchiveMonthResult(0, AvailableAtSource: false);

        var fromMs = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(new DateOnly(year, month, 1).AddMonths(1), TimeOnly.MinValue, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var parsed = rows
            .Select(r => (Ts: ArchiveCsv.NormalizeTimestampMs(long.Parse(r[0], CultureInfo.InvariantCulture)), Row: r))
            .Where(x => x.Ts >= fromMs && x.Ts < toMs)
            .OrderBy(x => x.Ts)
            .ToList();

        if (parsed.Count == 0)
        {
            // Archive HAD the file(s) but nothing landed in-range — distinct from a 404;
            // report available so job diagnostics don't misread "present but empty" as "absent".
            logger.LogWarning("{Dataset} {Symbol} {Year}-{Month:D2}: archive present but 0 in-range rows",
                dataset, asset.Venue.ApiSymbol, year, month);
            return new ArchiveMonthResult(0, AvailableAtSource: true);
        }

        var primaryFeed = feedName == FeedNames.Candles ? FeedNames.Candles : FeedNames.MarkPrice;
        var primaryPath = Path.Combine(assetDir, primaryFeed, $"{year:D4}-{month:D2}_{interval}.csv");
        var previousRows = await ArchiveStatusMerger.CountDataRows(primaryPath, ct);

        // Replace-guard: a sparse archive month must not clobber a fuller REST-collected one.
        if (parsed.Count < previousRows)
        {
            logger.LogWarning(
                "{Feed}/{Interval} {Year}-{Month:D2} {Symbol}: archive month has {New} rows < existing {Prev}; skipping replace",
                feedName, interval, year, month, asset.Venue.ApiSymbol, parsed.Count, previousRows);
            return new ArchiveMonthResult(0, AvailableAtSource: true);
        }

        long written = feedName == FeedNames.Candles
            ? await WriteCandles(asset, assetDir, interval, year, month, parsed, ct)
            : await WriteMarkPrice(assetDir, interval, year, month, parsed, ct);
        var delta = written - previousRows;

        var intervalMs = (long)IntervalParser.ToTimeSpan(feed.Interval).TotalMilliseconds;
        var gaps = ArchiveStatusMerger.DetectGaps(parsed, intervalMs);

        await ArchiveStatusMerger.MergeStatus(
            feedStatusStore, assetDir, primaryFeed, interval, parsed[0].Ts, parsed[^1].Ts, delta, gaps, ct);
        // candle-ext is rewritten in tandem with candles, so the same delta applies.
        if (feedName == FeedNames.Candles && AssetTypes.IsFutures(asset.Venue.AssetType))
            await ArchiveStatusMerger.MergeStatus(
                feedStatusStore, assetDir, FeedNames.CandleExt, interval, parsed[0].Ts, parsed[^1].Ts, delta, gaps, ct);

        logger.LogInformation("Materialized {Feed}/{Interval} {Year}-{Month:D2} for {Symbol}: {Rows} rows",
            feedName, interval, year, month, asset.Venue.ApiSymbol, written);
        return new ArchiveMonthResult(written, AvailableAtSource: true);
    }

    private async Task<long> WriteCandles(
        CollectionAsset asset, string assetDir, string interval,
        int year, int month, List<(long Ts, string[] Row)> parsed, CancellationToken ct)
    {
        await schemaManager.EnsureCandleConfig(assetDir, asset.DecimalDigits, interval, ct);

        var multiplier = (decimal)Math.Pow(10, asset.DecimalDigits);
        var candlePath = Path.Combine(assetDir, "candles", $"{year:D4}-{month:D2}_{interval}.csv");

        var candleRows = parsed.Select(x =>
        {
            var r = x.Row;
            var o = MoneyConvert.ToLong(decimal.Parse(r[1], CultureInfo.InvariantCulture) * multiplier);
            var h = MoneyConvert.ToLong(decimal.Parse(r[2], CultureInfo.InvariantCulture) * multiplier);
            var l = MoneyConvert.ToLong(decimal.Parse(r[3], CultureInfo.InvariantCulture) * multiplier);
            var c = MoneyConvert.ToLong(decimal.Parse(r[4], CultureInfo.InvariantCulture) * multiplier);
            var v = MoneyConvert.ToLong(decimal.Parse(r[5], CultureInfo.InvariantCulture) * multiplier);
            return $"{x.Ts},{o},{h},{l},{c},{v}";
        });

        await partitionWriter.ReplacePartition(candlePath, "ts,o,h,l,c,vol", candleRows, ct);

        if (AssetTypes.IsFutures(asset.Venue.AssetType))
        {
            await schemaManager.EnsureSchema(assetDir, FeedNames.CandleExt, interval, ExtColumns, ct: ct);
            var extPath = Path.Combine(assetDir, FeedNames.CandleExt, $"{year:D4}-{month:D2}_{interval}.csv");

            var extRows = parsed.Select(x =>
            {
                var r = x.Row;
                var vol = double.Parse(r[5], CultureInfo.InvariantCulture);
                var qv  = double.Parse(r[7], CultureInfo.InvariantCulture);
                var tc  = double.Parse(r[8], CultureInfo.InvariantCulture);
                var tb  = double.Parse(r[9], CultureInfo.InvariantCulture);
                var tbQv = double.Parse(r[10], CultureInfo.InvariantCulture);
                var proxy = vol > 0 ? Math.Clamp(tc * tb / vol, 0, tc) : 0;
                return $"{x.Ts},{qv.ToString(CultureInfo.InvariantCulture)},{tc.ToString(CultureInfo.InvariantCulture)},{tb.ToString(CultureInfo.InvariantCulture)},{tbQv.ToString(CultureInfo.InvariantCulture)},{proxy.ToString(CultureInfo.InvariantCulture)}";
            });

            await partitionWriter.ReplacePartition(extPath, $"ts,{string.Join(",", ExtColumns)}", extRows, ct);
        }

        return parsed.Count;
    }

    private async Task<long> WriteMarkPrice(
        string assetDir, string interval,
        int year, int month, List<(long Ts, string[] Row)> parsed, CancellationToken ct)
    {
        var columns = new[] { "o", "h", "l", "c" };
        await schemaManager.EnsureSchema(assetDir, FeedNames.MarkPrice, interval, columns, ct: ct);

        var markPath = Path.Combine(assetDir, FeedNames.MarkPrice, $"{year:D4}-{month:D2}_{interval}.csv");

        var rows = parsed.Select(x =>
        {
            var r = x.Row;
            var o = double.Parse(r[1], CultureInfo.InvariantCulture);
            var h = double.Parse(r[2], CultureInfo.InvariantCulture);
            var l = double.Parse(r[3], CultureInfo.InvariantCulture);
            var c = double.Parse(r[4], CultureInfo.InvariantCulture);
            return $"{x.Ts},{o.ToString(CultureInfo.InvariantCulture)},{h.ToString(CultureInfo.InvariantCulture)},{l.ToString(CultureInfo.InvariantCulture)},{c.ToString(CultureInfo.InvariantCulture)}";
        });

        await partitionWriter.ReplacePartition(markPath, "ts,o,h,l,c", rows, ct);
        return parsed.Count;
    }

}
