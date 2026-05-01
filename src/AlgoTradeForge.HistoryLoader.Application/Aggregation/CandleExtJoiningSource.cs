using System.Globalization;
using AlgoTradeForge.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Phase 2b — streaming 1:1 join between a chronological time-bar source and the asset's
/// <c>candle-ext</c> feed (TRD §6.2). Produces <see cref="SourceRecord"/>s with
/// <see cref="SourceRecord.BuyVolumeLong"/> / <see cref="SourceRecord.SellVolumeLong"/>
/// populated from <c>taker_buy_vol</c> for the EqI proxy accumulator.
/// </summary>
/// <remarks>
/// <para>
/// Walks both streams in lockstep — the source iterator is the driver; this decorator advances
/// a per-month file cursor through <c>&lt;assetDir&gt;/candle-ext/&lt;YYYY-MM&gt;_&lt;interval&gt;.csv</c>
/// to find the matching <c>ts</c>. Memory: O(1) per side (one open file handle, one row buffer).
/// </para>
/// <para>
/// Partial coverage (TRD §6.2): source records without a matching <c>candle-ext</c> row are
/// dropped silently. Spot assets and other no-candle-ext layouts are rejected upstream by
/// <see cref="EligibilityRules"/>; reaching this iterator with an empty join is a misconfiguration
/// signal but not a hard error — the resulting bar count of 0 + manifest entry surfaces it.
/// </para>
/// <para>
/// Self-contained CSV parsing rather than reusing <c>CsvFeedSeriesLoader</c> because the latter
/// lives in <c>AlgoTradeForge.Infrastructure</c> (no upward project reference from here) and
/// because this hot path benefits from emitting <see cref="SourceRecord"/>s directly without
/// allocating an intermediate <c>FeedSeries</c> for the entire date range.
/// </para>
/// </remarks>
public sealed class CandleExtJoiningSource
{
    private const string TakerBuyVolColumn = "taker_buy_vol";

    private readonly string _candleExtDir;
    private readonly string _interval;
    private readonly decimal _quantityScale;

    public CandleExtJoiningSource(string assetDir, string interval, ScaleContext scale)
    {
        if (string.IsNullOrEmpty(interval))
            throw new ArgumentException("Time-bar EqI requires a source interval (e.g. \"1m\"); the join keys " +
                "candle-ext partitions by interval.", nameof(interval));

        _candleExtDir = Path.Combine(assetDir, "candle-ext");
        _interval = interval;
        _quantityScale = scale.QuantityScale;
    }

    /// <summary>
    /// Yields source records joined to candle-ext. Records without a matching candle-ext row
    /// (by <c>ts</c>) are dropped — see TRD §6.2 partial-coverage rule.
    /// </summary>
    public IEnumerable<SourceRecord> Join(IEnumerable<SourceRecord> upstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);

        if (!Directory.Exists(_candleExtDir))
            yield break;

        using var cursor = new CandleExtCursor(_candleExtDir, _interval);

        foreach (var record in upstream)
        {
            if (!cursor.AdvanceTo(record.TsMs, out var takerBuyDouble))
                continue;       // partial coverage — skip this source record

            // Convert taker_buy_vol (raw double, side-feed convention §3.6) to scaled long
            // at the sum site (TRD §3.6 / §6.3). The conversion path is exactly:
            //   taker_buy_long = MoneyConvert.ToLong(taker_buy_double * QuantityScale)
            var takerBuyLong = MoneyConvert.ToLong((decimal)takerBuyDouble * _quantityScale);

            // Clamp into [0, Volume]: candle-ext occasionally reports floating-point taker_buy
            // marginally over the integer-rounded source vol, which would otherwise produce a
            // negative SellVolumeLong. The clamp matches the underlying invariant
            // taker_buy_vol ≤ vol — feeds that violate this are a Binance-side data issue.
            if (takerBuyLong < 0L) takerBuyLong = 0L;
            if (takerBuyLong > record.Volume) takerBuyLong = record.Volume;
            var sellLong = record.Volume - takerBuyLong;

            yield return record with { BuyVolumeLong = takerBuyLong, SellVolumeLong = sellLong };
        }
    }

    /// <summary>
    /// Walks candle-ext monthly partitions forward by ts. State machine:
    ///  • Closed → opens the partition file containing the requested ts.
    ///  • Open   → advances the line cursor to the row with ts == requested or rolls forward
    ///             to the next month's file when ts crosses a month boundary.
    /// Single-pass; not thread-safe.
    /// </summary>
    private sealed class CandleExtCursor : IDisposable
    {
        private readonly string _dir;
        private readonly string _interval;

        private FileStream? _fs;
        private StreamReader? _reader;
        private string? _openMonth;
        private int _takerBuyVolIdx = -1;       // absolute column index in CSV `parts` array (0 = ts)
        private long _stagedTs = long.MinValue;
        private double _stagedTakerBuyDouble;
        private bool _hasStaged;
        private bool _eofReached;

        public CandleExtCursor(string dir, string interval)
        {
            _dir = dir;
            _interval = interval;
        }

        public bool AdvanceTo(long ts, out double takerBuyDouble)
        {
            takerBuyDouble = 0d;

            // Walk past any staged record whose ts is older than the request — the upstream
            // iterator is monotonic, so once we step past a ts we never need to rewind.
            while (true)
            {
                if (!_hasStaged)
                {
                    if (!ReadNextRow()) return false;
                }

                if (_stagedTs == ts)
                {
                    takerBuyDouble = _stagedTakerBuyDouble;
                    _hasStaged = false;       // consume
                    return true;
                }

                if (_stagedTs > ts)
                    return false;             // candle-ext jumped past the requested ts (gap on source side)

                // _stagedTs < ts — advance.
                _hasStaged = false;
            }
        }

        private bool ReadNextRow()
        {
            while (true)
            {
                if (_reader is null)
                {
                    if (_eofReached) return false;
                    if (!OpenNextMonth()) return false;
                }

                var line = _reader!.ReadLine();
                if (line is null)
                {
                    CloseCurrent();
                    if (!OpenNextMonth())
                    {
                        _eofReached = true;
                        return false;
                    }
                    continue;
                }

                if (line.Length == 0) continue;

                var parts = line.Split(',');
                if (parts.Length <= _takerBuyVolIdx)
                    continue;       // malformed row — silently skip; primary partition is the source of truth

                if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts))
                    continue;
                if (!double.TryParse(parts[_takerBuyVolIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out var takerBuy))
                    continue;

                _stagedTs = ts;
                _stagedTakerBuyDouble = takerBuy;
                _hasStaged = true;
                return true;
            }
        }

        private bool OpenNextMonth()
        {
            // Enumerate available month files lex-sorted (≡ chronological for YYYY-MM); start
            // after the previously-open month (if any) and pick the first one ≥ that.
            var pattern = $"*_{_interval}.csv";
            var months = Directory
                .EnumerateFiles(_dir, pattern, SearchOption.TopDirectoryOnly)
                .Select(p => (Path: p, Name: Path.GetFileNameWithoutExtension(p)))
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToArray();

            (string Path, string Name)? next = null;
            foreach (var m in months)
            {
                // Name format: "YYYY-MM_<interval>"; the YYYY-MM prefix is what we sort on.
                var monthKey = m.Name.Length >= 7 ? m.Name[..7] : m.Name;
                if (_openMonth is not null && string.CompareOrdinal(monthKey, _openMonth) <= 0)
                    continue;

                next = (m.Path, monthKey);
                break;
            }

            if (next is null) return false;

            _fs = new FileStream(next.Value.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _reader = new StreamReader(_fs);
            _openMonth = next.Value.Name;

            // Header: "ts,col1,col2,..." → resolve taker_buy_vol's absolute column index in
            // the CSV row's `parts` array (0 = ts). ReadNextRow indexes parts[_takerBuyVolIdx]
            // directly without offset arithmetic.
            var header = _reader.ReadLine();
            if (header is null)
            {
                CloseCurrent();
                return OpenNextMonth();
            }

            var headerParts = header.Split(',');
            _takerBuyVolIdx = -1;
            for (var i = 1; i < headerParts.Length; i++)
            {
                if (string.Equals(headerParts[i], TakerBuyVolColumn, StringComparison.Ordinal))
                {
                    _takerBuyVolIdx = i;
                    break;
                }
            }

            if (_takerBuyVolIdx < 0)
                throw new InvalidOperationException(
                    $"candle-ext partition '{next.Value.Path}' is missing the '{TakerBuyVolColumn}' column. " +
                    $"Time-bar EqI requires it (TRD §6.3 m1_taker_buy_proxy reconstruction).");

            return true;
        }

        private void CloseCurrent()
        {
            _reader?.Dispose();
            _fs?.Dispose();
            _reader = null;
            _fs = null;
        }

        public void Dispose() => CloseCurrent();
    }
}
