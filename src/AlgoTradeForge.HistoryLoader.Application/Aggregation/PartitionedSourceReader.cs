using System.Runtime.CompilerServices;
using AlgoTradeForge.Application.IO;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Lazily yields <see cref="SourceRecord"/>s from a partitioned CSV feed in chronological order
/// via <see cref="IFileStorage"/>. Time-bar (monthly), tick (daily), and alt-bar (re-aggregation)
/// sources are streamed record-by-record so peak working set stays bounded regardless of span.
/// </summary>
public sealed class PartitionedSourceReader
{
    private readonly IFileStorage _storage;

    public PartitionedSourceReader(IFileStorage storage)
    {
        _storage = storage;
    }

    /// <summary>
    /// Yields source records inside <paramref name="from"/>..<paramref name="to"/> inclusive
    /// (ts in milliseconds). Malformed rows throw <see cref="FormatException"/> with file/row/
    /// column context — silent skipping would shift threshold boundaries and produce structurally
    /// different alt-bars than the user expects.
    /// </summary>
    public IAsyncEnumerable<SourceRecord> Read(
        DataFeedDescriptor source,
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken ct = default)
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
            DataFeedKind.TimeBar => ReadTimeBars(source, fromMs, toMs, ct),
            DataFeedKind.Tick => ReadTicks(source, fromMs, toMs, ct),
            DataFeedKind.AltBar => ReadAltBars(source, fromMs, toMs, ct),
            _ => throw new NotSupportedException(
                $"Source reader supports TimeBar, Tick, and AltBar; got Kind={source.Kind}. " +
                $"Side sources are not re-aggregatable through this reader."),
        };
    }

    private async IAsyncEnumerable<SourceRecord> ReadAltBars(
        DataFeedDescriptor source,
        long fromMs,
        long toMs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var dir = Path.Combine(source.DataRoot, source.Exchange, source.Asset, "aggregated", source.FeedId);

        // Lex sort = chronological: partitions are calendar-stamped and pNN files sort after their bare month.
        var files = await CollectCsvFilesAsync(dir, recursive: false, ct);
        if (files.Count == 0) yield break;
        PartitionFilenameParser.EnsureNoDuplicateMonthPartitions(files);

        foreach (var filePath in files)
        {
            await foreach (var record in ReadTimeBarFile(filePath, fromMs, toMs, ct))
                yield return record;
        }
    }

    private async IAsyncEnumerable<SourceRecord> ReadTimeBars(
        DataFeedDescriptor source,
        long fromMs,
        long toMs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var dir = Path.Combine(source.DataRoot, source.Exchange, source.Asset, "candles");

        // Per-FeedId suffix prevents cross-interval contamination — loading "1m" must NOT pick up "2026-04_5m.csv".
        var suffix = $"_{source.FeedId}.csv";
        var files = await CollectFilesAsync(dir, suffix, recursive: false, ct);
        if (files.Count == 0) yield break;
        PartitionFilenameParser.EnsureNoDuplicateMonthPartitions(files);

        foreach (var filePath in files)
        {
            await foreach (var record in ReadTimeBarFile(filePath, fromMs, toMs, ct))
                yield return record;
        }
    }

    private async IAsyncEnumerable<SourceRecord> ReadTimeBarFile(
        string filePath,
        long fromMs,
        long toMs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var rowIndex = 0;
        var firstLine = true;
        await foreach (var line in _storage.ReadLines(filePath, ct))
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

    private async IAsyncEnumerable<SourceRecord> ReadTicks(
        DataFeedDescriptor source,
        long fromMs,
        long toMs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var dir = Path.Combine(source.DataRoot, source.Exchange, source.Asset, "ticks");

        // Daily files (YYYY-MM-DD.csv) lex-sort = chronological.
        var files = await CollectFilesAsync(dir, ".csv", recursive: false, ct);
        files = files.Where(f =>
        {
            var name = Path.GetFileName(f);
            return name.Length == 14
                && name[4] == '-' && name[7] == '-'
                && name.EndsWith(".csv", StringComparison.Ordinal);
        }).ToList();
        if (files.Count == 0) yield break;

        foreach (var filePath in files)
        {
            await foreach (var record in ReadTickFile(filePath, fromMs, toMs, ct))
                yield return record;
        }
    }

    private async IAsyncEnumerable<SourceRecord> ReadTickFile(
        string filePath,
        long fromMs,
        long toMs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var rowIndex = 0;
        var firstLine = true;
        await foreach (var line in _storage.ReadLines(filePath, ct))
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

    private async Task<List<string>> CollectCsvFilesAsync(string dir, bool recursive, CancellationToken ct) =>
        await CollectFilesAsync(dir, ".csv", recursive, ct);

    private async Task<List<string>> CollectFilesAsync(string dir, string suffix, bool recursive, CancellationToken ct)
    {
        var files = new List<string>();
        await foreach (var key in _storage.ListKeys(dir, suffix, recursive, ct))
            files.Add(key);
        files.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));
        return files;
    }

    private static FormatException MalformedCell(string filePath, int rowIndex, string column, string raw) =>
        new($"Malformed source cell '{raw}' in '{filePath}' (row {rowIndex}, column '{column}'): expected long.");
}
