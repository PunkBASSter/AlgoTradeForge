using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Lazily yields <see cref="SourceRecord"/>s from a partitioned CSV feed in chronological order.
/// Mirrors <c>PartitionedCsvBarLoader</c>'s path/glob resolution but streams record-by-record
/// to keep the aggregator's peak working set bounded regardless of source span. Supports
/// time-bar (monthly), tick (daily), and alt-bar (re-aggregation) sources.
/// </summary>
public sealed class PartitionedSourceReader
{
    /// <summary>
    /// Yields source records inside <paramref name="from"/>..<paramref name="to"/> inclusive
    /// (ts in milliseconds). Malformed rows throw <see cref="FormatException"/> with file/row/
    /// column context — silent skipping would shift threshold boundaries and produce structurally
    /// different alt-bars than the user expects.
    /// </summary>
    public IEnumerable<SourceRecord> Read(
        DataFeedDescriptor source,
        DateOnly? from = null,
        DateOnly? to = null)
    {
        var fromMs = (from ?? DateOnly.MinValue) == DateOnly.MinValue
            ? long.MinValue
            : new DateTimeOffset(from!.Value.Year, from.Value.Month, from.Value.Day,
                0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var toMs = (to ?? DateOnly.MaxValue) == DateOnly.MaxValue
            ? long.MaxValue
            : new DateTimeOffset(to!.Value.Year, to.Value.Month, to.Value.Day,
                0, 0, 0, TimeSpan.Zero).AddDays(1).ToUnixTimeMilliseconds() - 1;

        return source.Kind switch
        {
            DataFeedKind.TimeBar => ReadTimeBars(source, fromMs, toMs),
            DataFeedKind.Tick => ReadTicks(source, fromMs, toMs),
            DataFeedKind.AltBar => ReadAltBars(source, fromMs, toMs),
            _ => throw new NotSupportedException(
                $"Source reader supports TimeBar, Tick, and AltBar; got Kind={source.Kind}. " +
                $"Side sources are not re-aggregatable through this reader."),
        };
    }

    private static IEnumerable<SourceRecord> ReadAltBars(DataFeedDescriptor source, long fromMs, long toMs)
    {
        var dir = Path.Combine(source.DataRoot, source.Exchange, source.Asset, "aggregated", source.FeedId);
        if (!Directory.Exists(dir))
            yield break;

        // Lex sort = chronological: partitions are calendar-stamped and pNN files sort after their bare month.
        var files = Directory
            .EnumerateFiles(dir, "*.csv", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

        PartitionFilenameParser.EnsureNoDuplicateMonthPartitions(files);

        foreach (var filePath in files)
        {
            // Same 6-col OHLCV shape as time bars; reuse parser. BuyVolumeLong/SellVolumeLong
            // stay 0 — safe-trio aggregators don't read them.
            foreach (var record in ReadTimeBarFile(filePath, fromMs, toMs))
                yield return record;
        }
    }

    private static IEnumerable<SourceRecord> ReadTimeBars(DataFeedDescriptor source, long fromMs, long toMs)
    {
        var dir = Path.Combine(source.DataRoot, source.Exchange, source.Asset, "candles");
        if (!Directory.Exists(dir))
            yield break;

        // Per-FeedId glob prevents cross-interval contamination — loading "1m" must NOT pick up "2026-04_5m.csv".
        var pattern = $"*_{source.FeedId}.csv";
        var files = Directory
            .EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

        PartitionFilenameParser.EnsureNoDuplicateMonthPartitions(files);

        foreach (var filePath in files)
        {
            foreach (var record in ReadTimeBarFile(filePath, fromMs, toMs))
                yield return record;
        }
    }

    private static IEnumerable<SourceRecord> ReadTimeBarFile(string filePath, long fromMs, long toMs)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(fs);

        string? line;
        var firstLine = true;
        var rowIndex = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            rowIndex++;
            if (firstLine) { firstLine = false; continue; }
            if (line.Length == 0) continue;

            var parts = line.Split(',');
            if (parts.Length < 6)
                throw new FormatException(
                    $"Malformed source row in '{filePath}' (row {rowIndex}): expected at least 6 comma-separated columns (ts,o,h,l,c,vol), got {parts.Length}.");

            if (!long.TryParse(parts[0], out var ts))
                throw MalformedCell(filePath, rowIndex, "ts", parts[0]);
            if (!long.TryParse(parts[1], out var open))
                throw MalformedCell(filePath, rowIndex, "o", parts[1]);
            if (!long.TryParse(parts[2], out var high))
                throw MalformedCell(filePath, rowIndex, "h", parts[2]);
            if (!long.TryParse(parts[3], out var low))
                throw MalformedCell(filePath, rowIndex, "l", parts[3]);
            if (!long.TryParse(parts[4], out var close))
                throw MalformedCell(filePath, rowIndex, "c", parts[4]);
            if (!long.TryParse(parts[5], out var volume))
                throw MalformedCell(filePath, rowIndex, "vol", parts[5]);

            if (ts < fromMs || ts > toMs) continue;

            yield return new SourceRecord(ts, open, high, low, close, volume);
        }
    }

    private static IEnumerable<SourceRecord> ReadTicks(DataFeedDescriptor source, long fromMs, long toMs)
    {
        var dir = Path.Combine(source.DataRoot, source.Exchange, source.Asset, "ticks");
        if (!Directory.Exists(dir))
            yield break;

        // Daily files lex-sort = chronological (ISO date YYYY-MM-DD).
        foreach (var filePath in Directory
                     .EnumerateFiles(dir, "????-??-??.csv", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            foreach (var record in ReadTickFile(filePath, fromMs, toMs))
                yield return record;
        }
    }

    private static IEnumerable<SourceRecord> ReadTickFile(string filePath, long fromMs, long toMs)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(fs);

        string? line;
        var firstLine = true;
        var rowIndex = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            rowIndex++;
            if (firstLine) { firstLine = false; continue; }
            if (line.Length == 0) continue;

            var parts = line.Split(',');
            if (parts.Length < 5)
                throw new FormatException(
                    $"Malformed tick row in '{filePath}' (row {rowIndex}): expected 5 comma-separated columns (ts,price,qty,is_buyer_maker,agg_id), got {parts.Length}.");

            if (!long.TryParse(parts[0], out var ts))
                throw MalformedCell(filePath, rowIndex, "ts", parts[0]);
            if (!long.TryParse(parts[1], out var price))
                throw MalformedCell(filePath, rowIndex, "price", parts[1]);
            if (!long.TryParse(parts[2], out var qty))
                throw MalformedCell(filePath, rowIndex, "qty", parts[2]);
            // is_buyer_maker drives EqIV's signed contribution: 0 = buy-aggressive (+qty), 1 = sell-aggressive (-qty).
            if (!int.TryParse(parts[3], out var isBuyerMaker) || (isBuyerMaker != 0 && isBuyerMaker != 1))
                throw MalformedCell(filePath, rowIndex, "is_buyer_maker", parts[3]);
            // agg_id is unused outside the ingestor's resume path.

            if (ts < fromMs || ts > toMs) continue;

            var buyLong = isBuyerMaker == 0 ? qty : 0L;
            var sellLong = isBuyerMaker == 1 ? qty : 0L;

            yield return new SourceRecord(
                ts, price, price, price, price, qty,
                BuyVolumeLong: buyLong, SellVolumeLong: sellLong);
        }
    }

    private static FormatException MalformedCell(string filePath, int rowIndex, string column, string raw) =>
        new($"Malformed source cell '{raw}' in '{filePath}' (row {rowIndex}, column '{column}'): expected long.");
}
