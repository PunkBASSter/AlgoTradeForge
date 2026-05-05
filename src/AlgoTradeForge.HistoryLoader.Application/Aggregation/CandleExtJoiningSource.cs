using System.Globalization;
using AlgoTradeForge.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Streaming 1:1 join between a chronological time-bar source and the asset's
/// <c>candle-ext</c> feed. Produces <see cref="SourceRecord"/>s with the appropriate
/// imbalance fields populated for the requested <see cref="CandleExtJoinMode"/>:
/// <list type="bullet">
///   <item><see cref="CandleExtJoinMode.TakerBuyVolume"/> (EqI proxy): reads
///         <c>taker_buy_vol</c>; writes <c>BuyVolumeLong</c>/<c>SellVolumeLong</c> in
///         base-asset-tick units (qty × QuantityScale).</item>
///   <item><see cref="CandleExtJoinMode.TakerBuyQuoteVolume"/> (EqID proxy): reads
///         <c>taker_buy_quote_vol</c>; writes <c>BuyVolumeLong</c>/<c>SellVolumeLong</c>
///         in dollar-tick units (dollars × QuantityScale × ScaleFactor) so the
///         EqIDAccumulator can sum directly.</item>
///   <item><see cref="CandleExtJoinMode.TakerBuyTradeCount"/> (EqIT proxy): reads
///         <c>taker_buy_trade_count</c> + <c>trade_count</c>; writes
///         <c>BuyTradeCountLong</c>/<c>SellTradeCountLong</c> as raw counts.</item>
/// </list>
/// Source records without a matching candle-ext row are dropped silently. O(1) memory.
/// </summary>
public sealed class CandleExtJoiningSource
{
    // Column-name lookup; the joiner resolves indices per partition (header row) so we
    // tolerate column-order changes between schema revisions.
    private const string TakerBuyVolColumn = "taker_buy_vol";
    private const string TakerBuyQuoteVolColumn = "taker_buy_quote_vol";
    private const string TakerBuyTradeCountColumn = "taker_buy_trade_count";
    private const string TradeCountColumn = "trade_count";

    private readonly string _candleExtDir;
    private readonly string _interval;
    private readonly decimal _quantityScale;
    private readonly decimal _quantityTimesScaleFactor;     // QuantityScale / TickSize
    private readonly CandleExtJoinMode _mode;

    public CandleExtJoiningSource(string assetDir, string interval, ScaleContext scale)
        : this(assetDir, interval, scale, CandleExtJoinMode.TakerBuyVolume)
    {
    }

    public CandleExtJoiningSource(
        string assetDir,
        string interval,
        ScaleContext scale,
        CandleExtJoinMode mode)
    {
        if (string.IsNullOrEmpty(interval))
            throw new ArgumentException("Time-bar imbalance proxies require a source interval (e.g. \"1m\"); the join keys " +
                "candle-ext partitions by interval.", nameof(interval));
        if (mode == CandleExtJoinMode.None)
            throw new ArgumentException("CandleExtJoiningSource requires a non-None join mode.", nameof(mode));

        _candleExtDir = Path.Combine(assetDir, "candle-ext");
        _interval = interval;
        _quantityScale = scale.QuantityScale;
        // dollar-tick = dollar × QuantityScale × ScaleFactor = dollar × QuantityScale / TickSize.
        _quantityTimesScaleFactor = scale.TickSize > 0m ? scale.QuantityScale / scale.TickSize : scale.QuantityScale;
        _mode = mode;
    }

    /// <summary>
    /// Yields source records joined to candle-ext. Records without a matching candle-ext row
    /// (by <c>ts</c>) are dropped.
    /// </summary>
    public IEnumerable<SourceRecord> Join(IEnumerable<SourceRecord> upstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);

        if (!Directory.Exists(_candleExtDir))
            yield break;

        using var cursor = new CandleExtCursor(_candleExtDir, _interval, _mode);

        foreach (var record in upstream)
        {
            if (!cursor.AdvanceTo(record.TsMs, out var row))
                continue;       // partial coverage — skip this source record

            switch (_mode)
            {
                case CandleExtJoinMode.TakerBuyVolume:
                {
                    var takerBuyLong = MoneyConvert.ToLong((decimal)row.PrimaryDouble * _quantityScale);
                    if (takerBuyLong < 0L) takerBuyLong = 0L;
                    if (takerBuyLong > record.Volume) takerBuyLong = record.Volume;
                    var sellLong = record.Volume - takerBuyLong;
                    yield return record with { BuyVolumeLong = takerBuyLong, SellVolumeLong = sellLong };
                    break;
                }
                case CandleExtJoinMode.TakerBuyQuoteVolume:
                {
                    // Pre-scale to dollar-tick units so EqIDAccumulator can sum without re-scaling.
                    // PrimaryDouble carries taker_buy_quote_vol; SecondaryDouble carries quote_vol
                    // (total quote volume) for the sell-side back-out.
                    var quoteVolDouble = row.SecondaryDouble;
                    var takerBuyQuote = row.PrimaryDouble;
                    if (takerBuyQuote < 0d) takerBuyQuote = 0d;
                    if (takerBuyQuote > quoteVolDouble && quoteVolDouble > 0d) takerBuyQuote = quoteVolDouble;
                    var sellQuote = quoteVolDouble - takerBuyQuote;
                    var buyDollarTick = MoneyConvert.ToLong((decimal)takerBuyQuote * _quantityTimesScaleFactor);
                    var sellDollarTick = MoneyConvert.ToLong((decimal)sellQuote * _quantityTimesScaleFactor);
                    yield return record with
                    {
                        BuyVolumeLong = buyDollarTick,
                        SellVolumeLong = sellDollarTick,
                    };
                    break;
                }
                case CandleExtJoinMode.TakerBuyTradeCount:
                {
                    // PrimaryDouble = taker_buy_trade_count; SecondaryDouble = trade_count.
                    var takerBuyCount = (long)row.PrimaryDouble;
                    var tradeCount = (long)row.SecondaryDouble;
                    if (takerBuyCount < 0L) takerBuyCount = 0L;
                    if (takerBuyCount > tradeCount) takerBuyCount = tradeCount;
                    var sellCount = tradeCount - takerBuyCount;
                    yield return record with
                    {
                        BuyTradeCountLong = takerBuyCount,
                        SellTradeCountLong = sellCount,
                    };
                    break;
                }
            }
        }
    }

    /// <summary>One staged candle-ext row, holding up to two doubles depending on join mode.</summary>
    private readonly record struct CursorRow(double PrimaryDouble, double SecondaryDouble);

    // Walks candle-ext monthly partitions forward by ts. Single-pass; not thread-safe.
    private sealed class CandleExtCursor : IDisposable
    {
        private readonly string _dir;
        private readonly string _interval;
        private readonly CandleExtJoinMode _mode;

        private FileStream? _fs;
        private StreamReader? _reader;
        private string? _openMonth;
        private int _primaryIdx = -1;
        private int _secondaryIdx = -1;
        private long _stagedTs = long.MinValue;
        private CursorRow _stagedRow;
        private bool _hasStaged;
        private bool _eofReached;

        public CandleExtCursor(string dir, string interval, CandleExtJoinMode mode)
        {
            _dir = dir;
            _interval = interval;
            _mode = mode;
        }

        public bool AdvanceTo(long ts, out CursorRow row)
        {
            row = default;

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
                    row = _stagedRow;
                    _hasStaged = false;
                    return true;
                }

                if (_stagedTs > ts)
                    return false;             // candle-ext jumped past the requested ts (gap on source side)

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
                var maxIdx = Math.Max(_primaryIdx, _secondaryIdx);
                if (parts.Length <= maxIdx)
                    continue;       // malformed row; primary partition is the source of truth

                if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts))
                    continue;
                if (!double.TryParse(parts[_primaryIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out var primary))
                    continue;
                var secondary = 0d;
                if (_secondaryIdx >= 0 &&
                    !double.TryParse(parts[_secondaryIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out secondary))
                    continue;

                _stagedTs = ts;
                _stagedRow = new CursorRow(primary, secondary);
                _hasStaged = true;
                return true;
            }
        }

        private bool OpenNextMonth()
        {
            // Lex sort ≡ chronological for YYYY-MM; start after the previously-open month.
            var pattern = $"*_{_interval}.csv";
            var months = Directory
                .EnumerateFiles(_dir, pattern, SearchOption.TopDirectoryOnly)
                .Select(p => (Path: p, Name: Path.GetFileNameWithoutExtension(p)))
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToArray();

            (string Path, string Name)? next = null;
            foreach (var m in months)
            {
                // Name format: "YYYY-MM_<interval>"; sort on the YYYY-MM prefix.
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

            var header = _reader.ReadLine();
            if (header is null)
            {
                CloseCurrent();
                return OpenNextMonth();
            }

            var headerParts = header.Split(',');
            var (primaryName, secondaryName) = _mode switch
            {
                CandleExtJoinMode.TakerBuyVolume => (TakerBuyVolColumn, (string?)null),
                CandleExtJoinMode.TakerBuyQuoteVolume => (TakerBuyQuoteVolColumn, "quote_vol"),
                CandleExtJoinMode.TakerBuyTradeCount => (TakerBuyTradeCountColumn, TradeCountColumn),
                _ => throw new InvalidOperationException($"Unsupported join mode: {_mode}"),
            };

            _primaryIdx = ResolveColumnIndex(headerParts, primaryName);
            _secondaryIdx = secondaryName is null ? -1 : ResolveColumnIndex(headerParts, secondaryName);

            if (_primaryIdx < 0)
                throw new InvalidOperationException(
                    $"candle-ext partition '{next.Value.Path}' is missing the '{primaryName}' column. " +
                    DescribeMissingRemediation(_mode));
            if (secondaryName is not null && _secondaryIdx < 0)
                throw new InvalidOperationException(
                    $"candle-ext partition '{next.Value.Path}' is missing the '{secondaryName}' column. " +
                    DescribeMissingRemediation(_mode));

            return true;
        }

        private static int ResolveColumnIndex(string[] headerParts, string columnName)
        {
            for (var i = 1; i < headerParts.Length; i++)
            {
                if (string.Equals(headerParts[i], columnName, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        private static string DescribeMissingRemediation(CandleExtJoinMode mode) => mode switch
        {
            CandleExtJoinMode.TakerBuyVolume =>
                "Time-bar EqI requires it for the m1_taker_buy_proxy reconstruction.",
            CandleExtJoinMode.TakerBuyQuoteVolume =>
                "Time-bar EqID requires it for the m1_taker_buy_quote_proxy reconstruction.",
            CandleExtJoinMode.TakerBuyTradeCount =>
                "Time-bar EqIT requires it for the m1_taker_buy_count_proxy reconstruction. " +
                "Re-fetch candle-ext to populate the new taker_buy_trade_count column.",
            _ => string.Empty,
        };

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
