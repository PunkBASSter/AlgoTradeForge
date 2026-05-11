using System.Globalization;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Storage;

/// <summary>
/// Writes Binance best bid/ask snapshots to daily-partitioned CSVs at
/// <c>{assetDir}/book-ticker/&lt;YYYY-MM-DD&gt;.csv</c> with schema
/// <c>ts,bid_price,bid_qty,ask_price,ask_qty,update_id</c>.
/// </summary>
/// <remarks>
/// Mirrors <c>DailyTickCsvWriter</c>: LRU dedup cache, active-day stream cache, torn-row
/// repair on resume. Dedup column is <c>update_id</c> (Values[4]) instead of <c>agg_id</c>.
/// The two writers duplicate ~70% of their plumbing — a future <c>DailyDedupCsvWriter</c>
/// base class is the natural consolidation point if a third daily-dedup feed is added.
/// </remarks>
internal sealed class DailyBookTickerCsvWriter : IBookTickerWriter, IDisposable
{
    private const int ValueCount = 5;  // [bid_price, bid_qty, ask_price, ask_qty, update_id]
    private const string Header = "ts,bid_price,bid_qty,ask_price,ask_qty,update_id";

    // Sized to cover spot+futures across a realistic asset catalog. With ~32 bytes per
    // (assetDir/dayKey, lastUpdateId) pair, 256 entries is ~8 KB. An LRU smaller than the
    // active asset count silently re-accepts old update_ids on the evicted asset, producing
    // duplicate rows.
    private const int MaxDedupCacheSize = 256;

    private readonly object _gate = new();

    private string? _activeDayKey;
    private FileStream? _activeFs;
    private StreamWriter? _activeSw;

    private readonly LinkedList<KeyValuePair<string, long>> _dedupLru = new();
    private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, long>>> _dedupIndex = new();

    public void Write(string assetDir, FeedRecord record)
    {
        if (record.Values.Length != ValueCount)
            throw new ArgumentException(
                $"BookTicker FeedRecord must have {ValueCount} values [bid_price, bid_qty, ask_price, ask_qty, update_id]; got {record.Values.Length}.",
                nameof(record));

        var dayKey = DateTimeOffset.FromUnixTimeMilliseconds(record.TimestampMs)
            .UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var dedupKey = $"{assetDir}/{dayKey}";

        lock (_gate)
        {
            // Binance update_ids are monotonic per-symbol; <= correctly skips replays.
            long updateId = (long)record.Values[4];
            if (TryGetLastUpdateId(dedupKey, out var lastId) && updateId <= lastId)
                return;

            if (_activeDayKey != dedupKey)
            {
                DisposeActiveCache();

                var feedDir = Path.Combine(assetDir, FeedNames.BookTicker);
                Directory.CreateDirectory(feedDir);
                var path = Path.Combine(feedDir, $"{dayKey}.csv");

                _activeFs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                _activeSw = new StreamWriter(_activeFs);
                _activeDayKey = dedupKey;

                if (_activeFs.Length == 0)
                    _activeSw.WriteLine(Header);
            }

            try
            {
                _activeSw!.Write(record.TimestampMs.ToString(CultureInfo.InvariantCulture));
                _activeSw.Write(',');
                _activeSw.Write(record.Values[0].ToString(CultureInfo.InvariantCulture));   // bid_price
                _activeSw.Write(',');
                _activeSw.Write(record.Values[1].ToString(CultureInfo.InvariantCulture));   // bid_qty
                _activeSw.Write(',');
                _activeSw.Write(record.Values[2].ToString(CultureInfo.InvariantCulture));   // ask_price
                _activeSw.Write(',');
                _activeSw.Write(record.Values[3].ToString(CultureInfo.InvariantCulture));   // ask_qty
                _activeSw.Write(',');
                _activeSw.WriteLine(updateId.ToString(CultureInfo.InvariantCulture));       // update_id

                _activeSw.Flush();

                SetLastUpdateId(dedupKey, updateId);
            }
            catch
            {
                DisposeActiveCache();
                throw;
            }
        }
    }

    public BookTickerResumeState? ResumeFrom(string assetDir)
    {
        var feedDir = Path.Combine(assetDir, FeedNames.BookTicker);
        if (!Directory.Exists(feedDir))
            return null;

        var files = Directory.GetFiles(feedDir, "????-??-??.csv")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .ToArray();

        if (files.Length == 0)
            return null;

        var latestFile = files[0];
        var dayKey = Path.GetFileNameWithoutExtension(latestFile);
        var dedupKey = $"{assetDir}/{dayKey}";

        lock (_gate)
        {
            if (_activeDayKey == dedupKey)
                DisposeActiveCache();

            using var fs = new FileStream(latestFile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

            for (int attempt = 0; attempt < 2; attempt++)
            {
                var (line, startOffset) = ReadLastLineWithOffset(fs);
                if (line is null)
                    return null;

                if (line.StartsWith("ts,", StringComparison.Ordinal)
                    || line.Equals("ts", StringComparison.OrdinalIgnoreCase))
                    return null;

                var parts = line.Split(',');
                if (parts.Length == 6
                    && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts)
                    && long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var updateId))
                {
                    SetLastUpdateId(dedupKey, updateId);
                    return new BookTickerResumeState(updateId, ts);
                }

                fs.SetLength(startOffset);
                fs.Flush();
            }

            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            DisposeActiveCache();
        }
    }

    private void DisposeActiveCache()
    {
        try { _activeSw?.Flush(); } catch { /* cleanup path */ }
        _activeSw?.Dispose();
        _activeFs?.Dispose();
        _activeSw = null;
        _activeFs = null;
        _activeDayKey = null;
    }

    private bool TryGetLastUpdateId(string dedupKey, out long updateId)
    {
        if (_dedupIndex.TryGetValue(dedupKey, out var node))
        {
            updateId = node.Value.Value;
            return true;
        }
        updateId = 0;
        return false;
    }

    private void SetLastUpdateId(string dedupKey, long updateId)
    {
        if (_dedupIndex.TryGetValue(dedupKey, out var node))
        {
            node.Value = new KeyValuePair<string, long>(dedupKey, updateId);
            _dedupLru.Remove(node);
            _dedupLru.AddFirst(node);
            return;
        }

        var newNode = _dedupLru.AddFirst(new KeyValuePair<string, long>(dedupKey, updateId));
        _dedupIndex[dedupKey] = newNode;

        if (_dedupLru.Count > MaxDedupCacheSize)
        {
            var last = _dedupLru.Last!;
            _dedupLru.RemoveLast();
            _dedupIndex.Remove(last.Value.Key);
        }
    }

    private static (string? Line, long StartOffset) ReadLastLineWithOffset(FileStream fs)
    {
        if (fs.Length == 0)
            return (null, 0);

        long endOffset = fs.Length;
        while (endOffset > 0)
        {
            fs.Position = endOffset - 1;
            int b = fs.ReadByte();
            if (b != '\n' && b != '\r')
                break;
            endOffset--;
        }

        if (endOffset == 0)
            return (null, 0);

        const int chunkSize = 256;
        var buffer = new byte[chunkSize];
        long startOffset = endOffset;

        while (startOffset > 0)
        {
            long readFrom = Math.Max(0, startOffset - chunkSize);
            int toRead = (int)(startOffset - readFrom);
            fs.Position = readFrom;
            fs.ReadExactly(buffer, 0, toRead);

            int found = -1;
            for (int i = toRead - 1; i >= 0; i--)
            {
                if (buffer[i] == (byte)'\n')
                {
                    found = i;
                    break;
                }
            }

            if (found >= 0)
            {
                startOffset = readFrom + found + 1;
                break;
            }

            startOffset = readFrom;
        }

        int len = (int)(endOffset - startOffset);
        var lineBytes = new byte[len];
        fs.Position = startOffset;
        fs.ReadExactly(lineBytes, 0, len);
        var line = System.Text.Encoding.UTF8.GetString(lineBytes).TrimEnd('\r', '\n');

        return (line, startOffset);
    }
}
