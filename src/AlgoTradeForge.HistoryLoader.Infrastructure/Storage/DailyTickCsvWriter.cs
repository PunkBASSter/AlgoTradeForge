using System.Globalization;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Storage;

/// <summary>
/// Writes Binance aggregate trades to daily-partitioned CSVs at
/// <c>{assetDir}/ticks/&lt;YYYY-MM-DD&gt;.csv</c> with schema
/// <c>ts,price,qty,is_buyer_maker,agg_id</c>.
/// </summary>
/// <remarks>
/// Dedup is by <c>agg_id</c>, not <c>ts</c> — multiple aggregated trades commonly share a
/// millisecond. <see cref="ResumeFrom"/> truncates a torn last row to its last clean <c>\n</c>
/// before parsing.
/// <para>
/// One <see cref="StreamWriter"/>+<see cref="FileStream"/> is cached for the active day to
/// avoid ~40 file-open syscalls/sec at sustained tick rates. A day swap or
/// <see cref="ResumeFrom"/> on the same day disposes the cache first (Windows file-share
/// rules forbid a second handle even within one process). Per-row Flush goes to OS buffer
/// (no fsync); torn-row repair in <see cref="ResumeFrom"/> is the crash-safety net.
/// </para>
/// <para>
/// The agg_id dedup table is an LRU capped at <see cref="MaxDedupCacheSize"/> — covers
/// UTC-boundary churn plus a few backfill days. Evicted days reseed via <see cref="ResumeFrom"/>
/// on next write. Prevents an unbounded (asset × day) leak.
/// </para>
/// </remarks>
internal sealed class DailyTickCsvWriter : ITickFeedWriter, IDisposable
{
    private const int TickValueCount = 4;  // [price, qty, is_buyer_maker, agg_id]
    private const string Header = "ts,price,qty,is_buyer_maker,agg_id";
    private const int MaxDedupCacheSize = 8;

    private readonly object _gate = new();

    // Active-day stream cache.
    private string? _activeDayKey;
    private FileStream? _activeFs;
    private StreamWriter? _activeSw;

    // LRU dedup cache; head = most-recently-touched.
    private readonly LinkedList<KeyValuePair<string, long>> _dedupLru = new();
    private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, long>>> _dedupIndex = new();

    public void Write(string assetDir, FeedRecord record)
    {
        if (record.Values.Length != TickValueCount)
            throw new ArgumentException(
                $"Tick FeedRecord must have {TickValueCount} values [price, qty, is_buyer_maker, agg_id]; got {record.Values.Length}.",
                nameof(record));

        var dayKey = DateTimeOffset.FromUnixTimeMilliseconds(record.TimestampMs)
            .UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var dedupKey = $"{assetDir}/{dayKey}";

        lock (_gate)
        {
            // Binance agg_ids are monotonic per-symbol; <= correctly skips replays and out-of-order arrivals.
            long aggId = (long)record.Values[3];
            if (TryGetLastAggId(dedupKey, out var lastAggId) && aggId <= lastAggId)
                return;

            if (_activeDayKey != dedupKey)
            {
                DisposeActiveCache();

                var feedDir = Path.Combine(assetDir, FeedNames.Ticks);
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
                _activeSw.Write(record.Values[0].ToString(CultureInfo.InvariantCulture));   // price
                _activeSw.Write(',');
                _activeSw.Write(record.Values[1].ToString(CultureInfo.InvariantCulture));   // qty
                _activeSw.Write(',');
                _activeSw.Write(((int)record.Values[2]).ToString(CultureInfo.InvariantCulture));  // is_buyer_maker (0/1)
                _activeSw.Write(',');
                _activeSw.WriteLine(aggId.ToString(CultureInfo.InvariantCulture));          // agg_id

                // Flush to OS buffer (not fsync); ResumeFrom's torn-row repair is the safety net.
                _activeSw.Flush();

                SetLastAggId(dedupKey, aggId);
            }
            catch
            {
                DisposeActiveCache();
                throw;
            }
        }
    }

    public TickResumeState? ResumeFrom(string assetDir)
    {
        var feedDir = Path.Combine(assetDir, FeedNames.Ticks);
        if (!Directory.Exists(feedDir))
            return null;

        // ISO date filenames lex-sort ≡ chronologically.
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
            // Dispose the active-day cache before opening the read handle — Windows file-share
            // rules reject FileAccess.ReadWrite while an Append+Write handle is open on the same
            // path even within one process.
            if (_activeDayKey == dedupKey)
                DisposeActiveCache();

            using var fs = new FileStream(latestFile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

            // Two attempts: read the tail; if torn, truncate and retry once.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var (line, startOffset) = ReadLastLineWithOffset(fs);
                if (line is null)
                    return null;

                if (line.StartsWith("ts,", StringComparison.Ordinal)
                    || line.Equals("ts", StringComparison.OrdinalIgnoreCase))
                    return null;

                var parts = line.Split(',');
                if (parts.Length == 5
                    && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts)
                    && long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var aggId))
                {
                    SetLastAggId(dedupKey, aggId);
                    return new TickResumeState(aggId, ts);
                }

                // Torn write: drop malformed trailing bytes and re-read.
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

    /// <summary>Disposes the cached active-day writer. Caller must hold <see cref="_gate"/>.</summary>
    private void DisposeActiveCache()
    {
        try { _activeSw?.Flush(); } catch { /* cleanup path */ }
        _activeSw?.Dispose();
        _activeFs?.Dispose();
        _activeSw = null;
        _activeFs = null;
        _activeDayKey = null;
    }

    private bool TryGetLastAggId(string dedupKey, out long aggId)
    {
        if (_dedupIndex.TryGetValue(dedupKey, out var node))
        {
            aggId = node.Value.Value;
            return true;
        }
        aggId = 0;
        return false;
    }

    private void SetLastAggId(string dedupKey, long aggId)
    {
        if (_dedupIndex.TryGetValue(dedupKey, out var node))
        {
            node.Value = new KeyValuePair<string, long>(dedupKey, aggId);
            _dedupLru.Remove(node);
            _dedupLru.AddFirst(node);
            return;
        }

        var newNode = _dedupLru.AddFirst(new KeyValuePair<string, long>(dedupKey, aggId));
        _dedupIndex[dedupKey] = newNode;

        if (_dedupLru.Count > MaxDedupCacheSize)
        {
            var last = _dedupLru.Last!;
            _dedupLru.RemoveLast();
            _dedupIndex.Remove(last.Value.Key);
        }
    }

    /// <summary>
    /// Returns the last (possibly torn) line and the byte offset where its content starts.
    /// Trailing <c>\r</c>/<c>\n</c> are stripped from the returned line.
    /// </summary>
    private static (string? Line, long StartOffset) ReadLastLineWithOffset(FileStream fs)
    {
        if (fs.Length == 0)
            return (null, 0);

        // Strip trailing newlines to find the end byte of the last meaningful line.
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

        // Scan backward in chunks for the previous '\n'; that byte+1 starts the line.
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
