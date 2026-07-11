using System.Globalization;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Archive;

internal sealed class TakerVolumeArchiveMaterializer(
    IBinanceArchiveClient archive,
    IPartitionFileWriter partitionWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<TakerVolumeArchiveMaterializer> logger) : IArchiveMaterializer
{
    private static readonly string[] Columns = ["buy_vol_usd", "sell_vol_usd", "ratio"];

    public string Exchange => "binance";
    public string FeedName => FeedNames.TakerVolume;
    // Futures-only: matches the retired live RatioCollectorService scope.
    public bool Supports(string assetType) => AssetTypes.IsFutures(assetType);

    public async Task<ArchiveMonthResult> MaterializeMonth(
        CollectionAsset asset, CollectionFeed feed,
        string assetDir, int year, int month, CancellationToken ct = default)
    {
        var interval = feed.Interval;
        var rows = new List<string[]>();
        var available = false;

        await using (var monthly = await archive.DownloadMonthly(
            "futures/um", "klines", asset.Venue.ApiSymbol, interval, year, month, ct))
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
            var monthStart = new DateOnly(year, month, 1);
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);
            for (var day = monthStart; day <= monthEnd; day = day.AddDays(1))
            {
                await using var daily = await archive.DownloadDaily(
                    "futures/um", "klines", asset.Venue.ApiSymbol, interval, day, ct);
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
            logger.LogWarning("taker-volume klines {Symbol} {Year}-{Month:D2}: archive present but 0 in-range rows",
                asset.Venue.ApiSymbol, year, month);
            return new ArchiveMonthResult(0, AvailableAtSource: true);
        }

        var partitionPath = Path.Combine(assetDir, FeedNames.TakerVolume, $"{year:D4}-{month:D2}_{interval}.csv");
        var previousRows = await ArchiveStatusMerger.CountDataRows(partitionPath, ct);

        await schemaManager.EnsureSchema(assetDir, FeedNames.TakerVolume, interval, Columns, ct: ct);

        var dataRows = parsed.Select(x =>
        {
            var r = x.Row;
            var buyVol = double.Parse(r[10], CultureInfo.InvariantCulture);
            var quoteVol = double.Parse(r[7], CultureInfo.InvariantCulture);
            // Clamp: near-equal float subtraction can yield a tiny negative.
            var sellVol = Math.Max(0d, quoteVol - buyVol);
            var ratio = sellVol > 0 ? buyVol / sellVol : 0d;
            return $"{x.Ts},{buyVol.ToString(CultureInfo.InvariantCulture)},{sellVol.ToString(CultureInfo.InvariantCulture)},{ratio.ToString(CultureInfo.InvariantCulture)}";
        });

        await partitionWriter.ReplacePartition(partitionPath, "ts,buy_vol_usd,sell_vol_usd,ratio", dataRows, ct);

        var delta = parsed.Count - previousRows;
        var intervalMs = (long)IntervalParser.ToTimeSpan(interval).TotalMilliseconds;
        var gaps = ArchiveStatusMerger.DetectGaps(parsed, intervalMs);

        await ArchiveStatusMerger.MergeStatus(
            feedStatusStore, assetDir, FeedNames.TakerVolume, interval,
            parsed[0].Ts, parsed[^1].Ts, delta, gaps, ct);

        logger.LogInformation("Materialized taker-volume/{Interval} {Year}-{Month:D2} for {Symbol}: {Rows} rows",
            interval, year, month, asset.Venue.ApiSymbol, parsed.Count);
        return new ArchiveMonthResult(parsed.Count, AvailableAtSource: true);
    }
}
