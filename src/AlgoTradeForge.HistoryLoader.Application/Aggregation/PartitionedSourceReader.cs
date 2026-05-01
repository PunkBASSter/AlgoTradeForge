using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Lazily yields <see cref="SourceRecord"/>s from a partitioned CSV feed in chronological
/// order (TRD §6.2). Mirrors the path / glob resolution of <c>PartitionedCsvBarLoader</c> but
/// streams record-by-record so the aggregator's peak working set stays bounded regardless of
/// source span (P1b-12 memory contract).
/// </summary>
/// <remarks>
/// Phase 1b shipped time-bar sources only (monthly partitions, 6-column OHLCV). Phase 2a adds
/// the tick path: daily partitions (<c>ticks/&lt;YYYY-MM-DD&gt;.csv</c>), 5-column rows
/// (<c>ts,price,qty,is_buyer_maker,agg_id</c>), mapped to <see cref="SourceRecord"/> with
/// <c>Open=High=Low=Close=price</c> and <c>Volume=qty</c>. <c>is_buyer_maker</c> and
/// <c>agg_id</c> are dropped at this layer — the EqI accumulator (Phase 2b) reads them via
/// a separate signed-aware reader.
///
/// The optional <c>candle-ext</c> 1:1 join (TRD §6.2 last sentence) is reserved for Phase 2b's
/// EqI proxy and not exposed here yet.
/// </remarks>
public sealed class PartitionedSourceReader
{
    /// <summary>
    /// Yields source records inside <paramref name="from"/>..<paramref name="to"/> inclusive.
    /// Filters by ts in milliseconds. Malformed rows (wrong column count, un-parseable cells)
    /// throw <see cref="FormatException"/> with file/row/column context — silent skipping would
    /// shift downstream threshold-equivalence boundaries and produce structurally different
    /// alt-bars than the user expects (P1b-0a parity with the side-feed loader).
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
            _ => throw new NotSupportedException(
                $"Source reader supports TimeBar and Tick; got Kind={source.Kind}. " +
                $"AltBar / Side sources are not re-aggregatable through this reader."),
        };
    }

    // -------------------------------------------------------------------------
    // Time-bar path (Phase 1b) — monthly partitions, 6-column OHLCV
    // -------------------------------------------------------------------------

    private static IEnumerable<SourceRecord> ReadTimeBars(DataFeedDescriptor source, long fromMs, long toMs)
    {
        var dir = Path.Combine(source.DataRoot, source.Exchange, source.Asset, "candles");
        if (!Directory.Exists(dir))
            yield break;

        // Per-FeedId glob avoids cross-interval contamination (P1a-29 / P1a-30 regression):
        // loading "1m" must NOT pick up "2026-04_5m.csv". Lex sort matches chronological because
        // months format as YYYY-MM and any future part-numbered overflow files (.pNN) sort after
        // their bare month within the same calendar.
        var pattern = $"*_{source.FeedId}.csv";
        foreach (var filePath in Directory
                     .EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
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

    // -------------------------------------------------------------------------
    // Tick path (Phase 2a) — daily partitions, 5-column [ts,price,qty,is_buyer_maker,agg_id]
    // -------------------------------------------------------------------------

    private static IEnumerable<SourceRecord> ReadTicks(DataFeedDescriptor source, long fromMs, long toMs)
    {
        var dir = Path.Combine(source.DataRoot, source.Exchange, source.Asset, "ticks");
        if (!Directory.Exists(dir))
            yield break;

        // Daily files lex-sort ≡ chronologically by ISO date (YYYY-MM-DD).
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
            // is_buyer_maker drives EqI's per-tick signed contribution (TRD §3.5):
            // is_buyer_maker=0 → buy-aggressive (+qty); =1 → sell-aggressive (-qty).
            // Populated unconditionally — EqV/EqT/EqD ignore the new SourceRecord fields, so
            // the cost is one branch + one struct field write per tick (no measurable overhead
            // vs. the parse cost itself).
            if (!int.TryParse(parts[3], out var isBuyerMaker) || (isBuyerMaker != 0 && isBuyerMaker != 1))
                throw MalformedCell(filePath, rowIndex, "is_buyer_maker", parts[3]);
            // agg_id intentionally not parsed — used only by the ingestor's resume-on-crash path.

            if (ts < fromMs || ts > toMs) continue;

            var buyLong = isBuyerMaker == 0 ? qty : 0L;
            var sellLong = isBuyerMaker == 1 ? qty : 0L;

            // Per-tick OHLC: price for all four fields; qty for volume.
            yield return new SourceRecord(
                ts, price, price, price, price, qty,
                BuyVolumeLong: buyLong, SellVolumeLong: sellLong);
        }
    }

    private static FormatException MalformedCell(string filePath, int rowIndex, string column, string raw) =>
        new($"Malformed source cell '{raw}' in '{filePath}' (row {rowIndex}, column '{column}'): expected long.");
}
