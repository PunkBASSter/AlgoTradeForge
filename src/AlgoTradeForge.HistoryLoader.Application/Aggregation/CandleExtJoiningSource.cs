using System.Globalization;
using System.Runtime.CompilerServices;
using AlgoTradeForge.Storage;
using AlgoTradeForge.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Streaming 1:1 join between a chronological time-bar source and the asset's
/// <c>candle-ext</c> feed. Produces <see cref="SourceRecord"/>s with the appropriate
/// imbalance fields populated for the requested <see cref="CandleExtJoinMode"/>.
/// Source records without a matching candle-ext row are dropped silently. O(1) memory.
/// </summary>
public sealed class CandleExtJoiningSource
{
    private const string TakerBuyVolColumn = "taker_buy_vol";
    private const string TakerBuyQuoteVolColumn = "taker_buy_quote_vol";
    private const string TakerBuyTradeCountColumn = "taker_buy_trade_count";
    private const string TradeCountColumn = "trade_count";

    private readonly IFileStorage _storage;
    private readonly string _candleExtDir;
    private readonly string _interval;
    private readonly decimal _quantityScale;
    private readonly decimal _quantityTimesScaleFactor;
    private readonly CandleExtJoinMode _mode;

    public CandleExtJoiningSource(
        IFileStorage storage,
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

        _storage = storage;
        _candleExtDir = Path.Combine(assetDir, "candle-ext");
        _interval = interval;
        _quantityScale = scale.QuantityScale;
        // dollar-tick = dollar × QuantityScale × ScaleFactor = dollar × QuantityScale / TickSize.
        _quantityTimesScaleFactor = scale.TickSize > 0m ? scale.QuantityScale / scale.TickSize : scale.QuantityScale;
        _mode = mode;
    }

    public async IAsyncEnumerable<SourceRecord> Join(
        IAsyncEnumerable<SourceRecord> upstream,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upstream);

        var partitionKeys = await ListPartitionsByMonth(ct);
        if (partitionKeys.Count == 0) yield break;

        await using var cursor = new CandleExtCursor(_storage, _interval, _mode, partitionKeys);

        await foreach (var record in upstream.WithCancellation(ct))
        {
            var (matched, row) = await cursor.AdvanceTo(record.TsMs, ct);
            if (!matched)
                continue;

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
                    // PrimaryDouble carries taker_buy_quote_vol; SecondaryDouble carries quote_vol.
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

    private async Task<List<(string Path, string MonthKey)>> ListPartitionsByMonth(CancellationToken ct)
    {
        var suffix = $"_{_interval}.csv";
        var matches = new List<(string Path, string MonthKey)>();
        await foreach (var key in _storage.ListKeys(_candleExtDir, suffix, recursive: false, ct))
        {
            var name = Path.GetFileNameWithoutExtension(key);
            var monthKey = name.Length >= 7 ? name[..7] : name;
            matches.Add((key, monthKey));
        }
        matches.Sort((a, b) => string.CompareOrdinal(a.MonthKey, b.MonthKey));
        return matches;
    }

    private readonly record struct CursorRow(double PrimaryDouble, double SecondaryDouble);

    // Walks candle-ext monthly partitions forward by ts. Single-pass; not thread-safe.
    private sealed class CandleExtCursor : IAsyncDisposable
    {
        private readonly IFileStorage _storage;
        private readonly string _interval;
        private readonly CandleExtJoinMode _mode;
        private readonly List<(string Path, string MonthKey)> _partitions;

        private int _partitionIndex = -1;
        private IAsyncEnumerator<string>? _lineEnumerator;
        private int _primaryIdx = -1;
        private int _secondaryIdx = -1;
        private long _stagedTs = long.MinValue;
        private CursorRow _stagedRow;
        private bool _hasStaged;
        private bool _eofReached;

        public CandleExtCursor(
            IFileStorage storage,
            string interval,
            CandleExtJoinMode mode,
            List<(string Path, string MonthKey)> partitions)
        {
            _storage = storage;
            _interval = interval;
            _mode = mode;
            _partitions = partitions;
        }

        public async Task<(bool Matched, CursorRow Row)> AdvanceTo(long ts, CancellationToken ct)
        {
            // Walk past any staged record whose ts is older than the request — the upstream
            // iterator is monotonic, so once we step past a ts we never need to rewind.
            while (true)
            {
                if (!_hasStaged)
                {
                    if (!await ReadNextRow(ct)) return (false, default);
                }

                if (_stagedTs == ts)
                {
                    var row = _stagedRow;
                    _hasStaged = false;
                    return (true, row);
                }

                if (_stagedTs > ts)
                    return (false, default);   // candle-ext jumped past the requested ts (gap on source side)

                _hasStaged = false;
            }
        }

        private async Task<bool> ReadNextRow(CancellationToken ct)
        {
            while (true)
            {
                if (_lineEnumerator is null)
                {
                    if (_eofReached) return false;
                    if (!await OpenNextMonth(ct)) return false;
                }

                if (!await _lineEnumerator!.MoveNextAsync())
                {
                    await CloseCurrent();
                    if (!await OpenNextMonth(ct))
                    {
                        _eofReached = true;
                        return false;
                    }
                    continue;
                }

                var line = _lineEnumerator.Current;
                if (line.Length == 0) continue;

                var parts = line.Split(',');
                var maxIdx = Math.Max(_primaryIdx, _secondaryIdx);
                if (parts.Length <= maxIdx)
                    continue;    // malformed row; primary partition is the source of truth

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

        private async Task<bool> OpenNextMonth(CancellationToken ct)
        {
            _partitionIndex++;
            if (_partitionIndex >= _partitions.Count) return false;

            var (path, _) = _partitions[_partitionIndex];
            _lineEnumerator = _storage.ReadLines(path, ct).GetAsyncEnumerator(ct);

            if (!await _lineEnumerator.MoveNextAsync())
            {
                await CloseCurrent();
                return await OpenNextMonth(ct);
            }
            var header = _lineEnumerator.Current;

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
                    $"candle-ext partition '{path}' is missing the '{primaryName}' column. " +
                    DescribeMissingRemediation(_mode));
            if (secondaryName is not null && _secondaryIdx < 0)
                throw new InvalidOperationException(
                    $"candle-ext partition '{path}' is missing the '{secondaryName}' column. " +
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
                "Time-bar EqIV requires it for the m1_taker_buy_proxy reconstruction.",
            CandleExtJoinMode.TakerBuyQuoteVolume =>
                "Time-bar EqID requires it for the m1_taker_buy_quote_proxy reconstruction.",
            CandleExtJoinMode.TakerBuyTradeCount =>
                "Time-bar EqIT requires it for the m1_taker_buy_count_proxy reconstruction. " +
                "Re-fetch candle-ext to populate the new taker_buy_trade_count column.",
            _ => string.Empty,
        };

        private async ValueTask CloseCurrent()
        {
            if (_lineEnumerator is not null)
            {
                await _lineEnumerator.DisposeAsync();
                _lineEnumerator = null;
            }
        }

        public async ValueTask DisposeAsync() => await CloseCurrent();
    }
}
