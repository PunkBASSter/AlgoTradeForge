using System.Globalization;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Storage;

/// <summary>
/// Writes Binance aggregate trades to daily-partitioned CSVs at
/// <c>{assetDir}/ticks/&lt;YYYY-MM-DD&gt;.csv</c> with schema
/// <c>ts,price,qty,is_buyer_maker,agg_id</c> (TRD §3.5).
/// </summary>
/// <remarks>
/// <para>
/// Dedup is by <c>agg_id</c>, not <c>ts</c> — multiple aggregated trades commonly share a
/// millisecond timestamp during high-volatility bursts. The collector advances by
/// <c>fromMs = lastTsMs</c> (inclusive) on resume; this writer drops any trade whose
/// <c>agg_id</c> is at-or-below the cached last-written id for that day.
/// </para>
/// <para>
/// Crash recovery: on first <see cref="ResumeFrom"/> call, the tail of the latest daily
/// partition is parsed. If the last row is malformed (torn write — process killed mid-line
/// before the trailing <c>\n</c>), the file is truncated to the last clean <c>\n</c> boundary
/// and the resume returns the previous (clean) row's id pair.
/// </para>
/// <para>
/// <b>Active-day stream cache.</b> One <see cref="StreamWriter"/> + <see cref="FileStream"/>
/// is kept open for the day currently being written. A swap to a new day flushes + disposes
/// the previous handle, then opens fresh. <see cref="ResumeFrom"/> for the same day disposes
/// the cache first (Windows file-share rules forbid a second handle even within one process).
/// At ~150k ticks/h sustained per asset, this eliminates ~40 file-open syscalls/sec; the
/// trade-off is per-row <see cref="StreamWriter.Flush"/> for crash-safety (managed→OS buffer,
/// no fsync; <see cref="ResumeFrom"/>'s torn-row repair remains the safety net).
/// </para>
/// <para>
/// <b>Bounded dedup cache.</b> The agg_id dedup table is an LRU capped at
/// <see cref="MaxDedupCacheSize"/> entries — covers UTC-boundary churn (current + previous
/// day) plus headroom for backfill jobs running across a few historical days. Older entries
/// fall out; if a backfill targets an evicted day, <see cref="ResumeFrom"/> reseeds the
/// entry from disk before the next <c>Write</c>. Prevents the slow leak that would otherwise
/// accumulate one entry per (asset × day) ever written.
/// </para>
/// </remarks>
internal sealed class DailyTickCsvWriter : ITickFeedWriter, IDisposable
{
    private const int TickValueCount = 4;  // [price, qty, is_buyer_maker, agg_id]
    private const string Header = "ts,price,qty,is_buyer_maker,agg_id";
    private const int MaxDedupCacheSize = 8;

    private readonly object _gate = new();

    // Active-day stream cache (M1).
    private string? _activeDayKey;
    private FileStream? _activeFs;
    private StreamWriter? _activeSw;

    // LRU-bounded dedup cache (M2). LinkedList head = most-recently-touched.
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
            // Dedup by agg_id within this day. Binance ids are monotonic per-symbol so a
            // <= comparison correctly skips both replays and out-of-order arrivals.
            long aggId = (long)record.Values[3];
            if (TryGetLastAggId(dedupKey, out var lastAggId) && aggId <= lastAggId)
                return;

            // Swap the cached writer if this Write targets a different day than the cache holds.
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

                // Flush to OS buffer for crash-safety. Not fsync — torn writes still possible,
                // but ResumeFrom's tail-repair handles that case.
                _activeSw.Flush();

                SetLastAggId(dedupKey, aggId);
            }
            catch
            {
                // Don't leave a broken handle in the cache slot.
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
            // If the cache holds the same day, dispose it before opening the read handle —
            // Windows file-share rules will reject FileAccess.ReadWrite while an Append+Write
            // handle is open on the same path even within one process.
            if (_activeDayKey == dedupKey)
                DisposeActiveCache();

            using var fs = new FileStream(latestFile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

            // Two attempts: first read the tail; if torn, truncate and retry once.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var (line, startOffset) = ReadLastLineWithOffset(fs);
                if (line is null)
                    return null;

                // Header line — file has only a header, no data yet.
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

                // Torn write: drop the malformed bytes and re-read.
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

    /// <summary>Disposes any cached active-day writer + stream. Caller must hold <see cref="_gate"/>.</summary>
    private void DisposeActiveCache()
    {
        try { _activeSw?.Flush(); } catch { /* swallow — we're cleaning up */ }
        _activeSw?.Dispose();
        _activeFs?.Dispose();
        _activeSw = null;
        _activeFs = null;
        _activeDayKey = null;
    }

    /// <summary>Reads dedup state for a day from the LRU. Caller must hold <see cref="_gate"/>.</summary>
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

    /// <summary>
    /// Updates dedup state for a day, moving the entry to LRU front. Evicts the oldest entry
    /// when capacity is exceeded. Caller must hold <see cref="_gate"/>.
    /// </summary>
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
    /// Returns the last newline-terminated (or unterminated, in the torn case) line of
    /// <paramref name="fs"/>, along with the byte offset where that line begins. Trailing
    /// <c>\r</c>/<c>\n</c>s are stripped from the returned line; the offset still points to
    /// the first byte of the line content (after any preceding <c>\n</c>).
    /// </summary>
    private static (string? Line, long StartOffset) ReadLastLineWithOffset(FileStream fs)
    {
        if (fs.Length == 0)
            return (null, 0);

        // Strip trailing newlines to find the inclusive end byte of the last meaningful line.
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

        // Scan backward in chunks for the previous '\n' — that byte+1 is the line's start.
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

        // Read [startOffset, endOffset) as the line content.
        int len = (int)(endOffset - startOffset);
        var lineBytes = new byte[len];
        fs.Position = startOffset;
        fs.ReadExactly(lineBytes, 0, len);
        var line = System.Text.Encoding.UTF8.GetString(lineBytes).TrimEnd('\r', '\n');

        return (line, startOffset);
    }
}
