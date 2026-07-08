using System.Globalization;
using AlgoTradeForge.Domain;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Archive;

internal sealed class AggTradesArchiveMaterializer(
    IBinanceArchiveClient archive,
    IPartitionFileWriter partitionWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<AggTradesArchiveMaterializer> logger) : IArchiveMaterializer
{
    private const string Header = "ts,price,qty,is_buyer_maker,agg_id";
    private static readonly string[] Columns = ["price", "qty", "is_buyer_maker", "agg_id"];

    public string Exchange => "binance";
    public string FeedName => FeedNames.Ticks;
    public bool Supports(string assetType) => true;

    public async Task<ArchiveMonthResult> MaterializeMonth(
        AssetCollectionConfig assetConfig, FeedCollectionConfig feedConfig,
        string assetDir, int year, int month, CancellationToken ct = default)
    {
        var market = AssetTypes.IsSpot(assetConfig.Type) ? "spot" : "futures/um";
        var symbol = assetConfig.Symbol;
        var multiplier = (decimal)Math.Pow(10, assetConfig.DecimalDigits);
        var previousRowsForMonth = await SumExistingMonthRows(assetDir, year, month, ct);

        var fromMs = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(new DateOnly(year, month, 1).AddMonths(1), TimeOnly.MinValue, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var lastSeenAggId = long.MinValue;
        long rowsWritten = 0;
        long firstTs = 0, lastTs = 0;
        var anyRow = false;
        var schemaEnsured = false;
        var available = false;
        var fromMonthlyZip = false;

        DateOnly? currentDay = null;
        var dayBuffer = new List<string>();

        async Task EnsureSchemaOnce()
        {
            if (schemaEnsured) return;
            schemaEnsured = true;
            await schemaManager.EnsureSchema(assetDir, FeedNames.Ticks, "", Columns, ct: ct);
        }

        async Task FlushDay()
        {
            if (currentDay is null || dayBuffer.Count == 0) return;
            var path = Path.Combine(assetDir, FeedNames.Ticks, $"{currentDay.Value:yyyy-MM-dd}.csv");
            await partitionWriter.ReplacePartition(path, Header, dayBuffer, ct);
            dayBuffer.Clear();
        }

        async Task ProcessReader(TextReader reader)
        {
            // aggTrades are GB-scale; stream + flush per UTC day, never buffer the month.
            foreach (var r in ArchiveCsv.ReadRows(reader))
            {
                ct.ThrowIfCancellationRequested();
                var aggId = long.Parse(r[0], CultureInfo.InvariantCulture);
                if (aggId <= lastSeenAggId) continue; // monotonic-watermark dedup (agg_ids strictly increase)
                lastSeenAggId = aggId;

                var ts = ArchiveCsv.NormalizeTimestampMs(long.Parse(r[5], CultureInfo.InvariantCulture));
                if (ts < fromMs || ts >= toMs) continue; // drop trailing/leading rows spilling from a neighbouring month

                var priceLong = MoneyConvert.ToLong(decimal.Parse(r[1], CultureInfo.InvariantCulture) * multiplier);
                var qtyLong = MoneyConvert.ToLong(decimal.Parse(r[2], CultureInfo.InvariantCulture) * multiplier);
                var isBuyerMaker = ParseBool(r[6]) ? 1 : 0;

                var day = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime);
                if (currentDay is not null && day < currentDay)
                    throw ArchiveIntegrityException.NonMonotonicArchive(
                        $"{symbol} ticks {year:D4}-{month:D2}: day {day:yyyy-MM-dd} follows {currentDay:yyyy-MM-dd}");
                if (currentDay is not null && day != currentDay)
                    await FlushDay();
                currentDay = day;

                dayBuffer.Add($"{ts},{priceLong},{qtyLong},{isBuyerMaker},{aggId}");
                if (!anyRow) { firstTs = ts; anyRow = true; }
                lastTs = ts;
                rowsWritten++;
            }
        }

        await using (var monthly = await archive.DownloadMonthly(market, "aggTrades", symbol, interval: null, year, month, ct))
        {
            if (monthly is not null)
            {
                available = true;
                fromMonthlyZip = true;
                await EnsureSchemaOnce();
                using var reader = new StreamReader(monthly);
                await ProcessReader(reader);
            }
        }

        if (!available)
        {
            // Closed months only (ownership rule); the caller never passes the current month.
            var monthStart = new DateOnly(year, month, 1);
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);
            for (var day = monthStart; day <= monthEnd; day = day.AddDays(1))
            {
                await using var daily = await archive.DownloadDaily(market, "aggTrades", symbol, interval: null, day, ct);
                if (daily is null) continue;
                available = true;
                await EnsureSchemaOnce();
                using var reader = new StreamReader(daily);
                await ProcessReader(reader);
            }
        }

        if (!available)
            return new ArchiveMonthResult(0, AvailableAtSource: false);

        await FlushDay();

        if (anyRow)
            await ArchiveStatusMerger.MergeStatus(
                feedStatusStore, assetDir, FeedNames.Ticks, "",
                firstTs, lastTs, rowsWritten - previousRowsForMonth, newGaps: [], ct);

        // An available-but-empty monthly zip (zero in-month rows) must NOT be marked complete —
        // it would be falsely covered on disk with no CSVs and never re-requested.
        if (fromMonthlyZip && anyRow)
            await ArchiveStatusMerger.MarkCompleteMonth(
                feedStatusStore, assetDir, FeedNames.Ticks, "", $"{year:D4}-{month:D2}", ct);

        logger.LogInformation("Materialized ticks {Year}-{Month:D2} for {Symbol}: {Rows} rows",
            year, month, symbol, rowsWritten);
        return new ArchiveMonthResult(rowsWritten, AvailableAtSource: true);
    }

    private static async Task<long> SumExistingMonthRows(string assetDir, int year, int month, CancellationToken ct)
    {
        long total = 0;
        var monthStart = new DateOnly(year, month, 1);
        var monthEnd = monthStart.AddMonths(1);
        for (var day = monthStart; day < monthEnd; day = day.AddDays(1))
            total += await ArchiveStatusMerger.CountDataRows(
                Path.Combine(assetDir, FeedNames.Ticks, $"{day:yyyy-MM-dd}.csv"), ct);
        return total;
    }

    private static bool ParseBool(string s) => s is "1" || (bool.TryParse(s, out var b) && b);
}
