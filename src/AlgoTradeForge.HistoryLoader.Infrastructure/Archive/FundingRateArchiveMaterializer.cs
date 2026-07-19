using System.Globalization;
using AlgoTradeForge.Domain;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Archive;

internal sealed class FundingRateArchiveMaterializer(
    IBinanceArchiveClient archive,
    IPartitionFileWriter partitionWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<FundingRateArchiveMaterializer> logger) : IArchiveMaterializer
{
    private const string Header = "ts,rate,mark_price";
    private static readonly string[] Columns = ["rate", "mark_price"];
    private const long EightHoursMs = 8L * 60 * 60 * 1000;

    public string Exchange => "binance";
    public string FeedName => FeedNames.FundingRate;
    public bool Supports(string assetType) => AssetTypes.IsFutures(assetType);

    public async Task<ArchiveMonthResult> MaterializeMonth(
        CollectionAsset asset, CollectionFeed feed,
        string assetDir, int year, int month, CancellationToken ct = default)
    {
        const string market = "futures/um";
        var symbol = asset.Venue.ApiSymbol;

        var (fundingRaw, fromMonthlyZip) = await Download(market, "fundingRate", interval: null, symbol, year, month, ct);
        if (fundingRaw.Count == 0)
            return new ArchiveMonthResult(0, AvailableAtSource: false);

        var fromMs = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(new DateOnly(year, month, 1).AddMonths(1), TimeOnly.MinValue, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var parsed = fundingRaw
            .Select(r => (Ts: ArchiveCsv.NormalizeTimestampMs(long.Parse(r[0], CultureInfo.InvariantCulture)), Row: r))
            .Where(x => x.Ts >= fromMs && x.Ts < toMs)
            .OrderBy(x => x.Ts)
            .ToList();

        if (parsed.Count == 0)
            return new ArchiveMonthResult(0, AvailableAtSource: true);

        var (markRaw, _) = await Download(market, "markPriceKlines", "8h", symbol, year, month, ct);
        var markClose = new Dictionary<long, double>();
        foreach (var r in markRaw)
        {
            var ts = ArchiveCsv.NormalizeTimestampMs(long.Parse(r[0], CultureInfo.InvariantCulture));
            markClose[ts] = double.Parse(r[4], CultureInfo.InvariantCulture);
        }

        // mark_price ≈ 8h-boundary close; carry the last-known close forward for a missing boundary.
        // A leading gap (no close yet) writes 0.0 — auto-apply consumers must tolerate it (logged once).
        var lastKnownClose = 0.0;
        var loggedLeadingZero = false;
        var outRows = new List<string>(parsed.Count);
        foreach (var x in parsed)
        {
            var rate = double.Parse(x.Row[2], CultureInfo.InvariantCulture);
            double markPrice;
            if (markClose.TryGetValue(x.Ts, out var close))
            {
                markPrice = close;
                lastKnownClose = close;
            }
            else
            {
                markPrice = lastKnownClose;
                if (lastKnownClose == 0.0 && !loggedLeadingZero)
                {
                    loggedLeadingZero = true;
                    logger.LogWarning(
                        "funding-rate {Year}-{Month:D2} {Symbol}: no mark close at/before {Ts}; leading mark_price=0.0",
                        year, month, symbol, x.Ts);
                }
            }
            outRows.Add($"{x.Ts},{rate.ToString(CultureInfo.InvariantCulture)},{markPrice.ToString(CultureInfo.InvariantCulture)}");
        }

        await schemaManager.EnsureSchema(
            assetDir, FeedNames.FundingRate, "", Columns,
            new AutoApplySpec("FundingRate", "rate"), ct);

        var path = Path.Combine(assetDir, FeedNames.FundingRate, $"{year:D4}-{month:D2}.csv");
        var previousRows = await ArchiveStatusMerger.CountDataRows(path, ct);
        await partitionWriter.ReplacePartition(path, Header, outRows, ct);

        var gaps = ArchiveStatusMerger.DetectGaps(parsed.Select(x => x.Ts).ToList(), EightHoursMs);
        await ArchiveStatusMerger.MergeStatus(
            feedStatusStore, assetDir, FeedNames.FundingRate, "",
            parsed[0].Ts, parsed[^1].Ts, outRows.Count - previousRows, gaps, ct);

        if (fromMonthlyZip)
            await ArchiveStatusMerger.MarkCompleteMonth(
                feedStatusStore, assetDir, FeedNames.FundingRate, "", $"{year:D4}-{month:D2}", ct);

        logger.LogInformation("Materialized funding-rate {Year}-{Month:D2} for {Symbol}: {Rows} rows",
            year, month, symbol, outRows.Count);
        return new ArchiveMonthResult(outRows.Count, AvailableAtSource: true);
    }

    private async Task<(List<string[]> Rows, bool FromMonthly)> Download(
        string market, string dataset, string? interval, string symbol,
        int year, int month, CancellationToken ct)
    {
        var rows = new List<string[]>();

        await using (var monthly = await archive.DownloadMonthly(market, dataset, symbol, interval, year, month, ct))
        {
            if (monthly is not null)
            {
                using var reader = new StreamReader(monthly);
                rows.AddRange(ArchiveCsv.ReadRows(reader));
                return (rows, true);
            }
        }

        // Closed months only (ownership rule); the caller never passes the current month.
        var monthStart = new DateOnly(year, month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        for (var day = monthStart; day <= monthEnd; day = day.AddDays(1))
        {
            await using var daily = await archive.DownloadDaily(market, dataset, symbol, interval, day, ct);
            if (daily is null) continue;
            using var reader = new StreamReader(daily);
            rows.AddRange(ArchiveCsv.ReadRows(reader));
        }

        return (rows, false);
    }
}
