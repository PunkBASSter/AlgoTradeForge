using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Indicators;

/// <summary>
/// Shared state machine for the ATR_MULTIPLE zigzag detectors (<see cref="AtrZigZag"/>,
/// <see cref="AtrZigZagTrend"/>): Wilder ATR with the reversal threshold pinned at the
/// extremum bar, bootstrap anchoring, and the high-first intrabar extend/confirm phase
/// logic. Pivots are written into the owner's "Value" buffer; the owner appends buffer
/// defaults before calling <see cref="Process"/>. See <see cref="AtrZigZag"/> for the
/// research provenance and the per-timeframe multiplier/period calibration table.
/// </summary>
internal sealed class AtrZigZagCore
{
    private readonly double _multiplier;
    private readonly int _atrPeriod;
    private readonly IndicatorBuffer<long> _value;
    private readonly Action<bool, long>? _onPivotConfirmed;
    private readonly Action<int>? _onBarProcessed;

    // Wilder ATR state
    private double _atr = double.NaN;
    private double _trSeedSum;
    private long _previousClose;
    // ATR per bar index, kept only until bootstrap completes (needed to pin the
    // extremum ATR when the bootstrap anchor lands on a past bar).
    private List<double>? _bootstrapAtrs = [];

    // Detector state
    private int _direction;          // 0 = bootstrap (undeclared), 1 = up, -1 = down
    private int _extIdx;
    private long _extPrice;
    private double _extAtr;
    private long _startHigh = long.MinValue;
    private long _startLow = long.MaxValue;
    private int _lastProcessedIndex = -1;

    /// <param name="onPivotConfirmed">Fires when a reversal confirms the previous swing:
    /// (isHigh, pivot price). Fires before the new in-progress extremum replaces it.</param>
    /// <param name="onBarProcessed">Fires once per processed bar (including ATR-warmup and
    /// bootstrap bars), after the detector state for that bar is final.</param>
    public AtrZigZagCore(
        double multiplier,
        int atrPeriod,
        IndicatorBuffer<long> value,
        Action<bool, long>? onPivotConfirmed = null,
        Action<int>? onBarProcessed = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(multiplier);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(atrPeriod);

        _multiplier = multiplier;
        _atrPeriod = atrPeriod;
        _value = value;
        _onPivotConfirmed = onPivotConfirmed;
        _onBarProcessed = onBarProcessed;
    }

    public int MinimumHistory => _atrPeriod + 1;

    /// <summary>0 = bootstrap (undeclared), 1 = up phase, -1 = down phase.</summary>
    public int Direction => _direction;

    /// <summary>Price of the in-progress extremum (valid once <see cref="Direction"/> != 0).</summary>
    public long ExtPrice => _extPrice;

    public void Process(IReadOnlyList<Int64Bar> series)
    {
        var startIndex = _lastProcessedIndex + 1;

        for (var i = startIndex; i < series.Count; i++)
        {
            ProcessBar(series, i);
            _onBarProcessed?.Invoke(i);
        }

        if (series.Count > 0)
            _lastProcessedIndex = series.Count - 1;
    }

    private void ProcessBar(IReadOnlyList<Int64Bar> series, int i)
    {
        var bar = series[i];
        UpdateAtr(bar, i);
        _bootstrapAtrs?.Add(_atr);

        if (double.IsNaN(_atr))
        {
            // ATR warmup: only track running extremes for the bootstrap phase
            TrackBootstrapExtremes(bar);
            return;
        }

        if (_direction == 0)
        {
            Bootstrap(series, bar, i);
            return;
        }

        if (_direction > 0)
        {
            // Extend-first in the up phase (high-first intrabar convention)
            if (bar.High > _extPrice)
            {
                if (_extIdx != i)
                    _value.Revise(_extIdx, 0L);

                _extIdx = i;
                _extPrice = bar.High;
                _extAtr = _atr;
                _value.Set(i, bar.High);
            }

            if ((double)(_extPrice - bar.Low) >= _multiplier * _extAtr && i - _extIdx >= 1)
            {
                // Reversal down confirmed: the high pivot stands, start a new low
                _onPivotConfirmed?.Invoke(true, _extPrice);
                _direction = -1;
                _extIdx = i;
                _extPrice = bar.Low;
                _extAtr = _atr;
                _value.Set(i, bar.Low);
            }
        }
        else
        {
            // Confirm-first in the down phase: an upward reversal against the
            // prior extreme wins over extending the low on the same bar
            if ((double)(bar.High - _extPrice) >= _multiplier * _extAtr && i - _extIdx >= 1)
            {
                _onPivotConfirmed?.Invoke(false, _extPrice);
                _direction = 1;
                _extIdx = i;
                _extPrice = bar.High;
                _extAtr = _atr;
                _value.Set(i, bar.High);
            }
            else if (bar.Low < _extPrice)
            {
                if (_extIdx != i)
                    _value.Revise(_extIdx, 0L);

                _extIdx = i;
                _extPrice = bar.Low;
                _extAtr = _atr;
                _value.Set(i, bar.Low);
            }
        }
    }

    private void Bootstrap(IReadOnlyList<Int64Bar> series, Int64Bar bar, int i)
    {
        // Direction is undeclared: breach multiplier*ATR(current) vs the running
        // min/max over PRIOR bars, then anchor at the true argmax/argmin over [0..i].
        if (_startHigh == long.MinValue)
        {
            // First evaluated bar with no prior extremes (atrPeriod == 1)
            TrackBootstrapExtremes(bar);
            return;
        }

        var threshold = _multiplier * _atr;
        var upMove = (double)(bar.High - _startLow);
        var downMove = (double)(_startHigh - bar.Low);

        if (upMove >= threshold)
        {
            _direction = 1;
            AnchorExtremum(series, i, anchorHigh: true);
        }
        else if (downMove >= threshold)
        {
            _direction = -1;
            AnchorExtremum(series, i, anchorHigh: false);
        }
        else
        {
            TrackBootstrapExtremes(bar);
        }
    }

    private void AnchorExtremum(IReadOnlyList<Int64Bar> series, int i, bool anchorHigh)
    {
        var extIdx = 0;
        var extPrice = anchorHigh ? series[0].High : series[0].Low;
        for (var j = 1; j <= i; j++)
        {
            var p = anchorHigh ? series[j].High : series[j].Low;
            if (anchorHigh ? p > extPrice : p < extPrice)
            {
                extIdx = j;
                extPrice = p;
            }
        }

        _extIdx = extIdx;
        _extPrice = extPrice;
        var anchorAtr = _bootstrapAtrs![extIdx];
        _extAtr = double.IsNaN(anchorAtr) ? _atr : anchorAtr;

        // The anchor usually lands on a past bar: write it through Revise so chart
        // emitters (buffer.OnRevised) see the retroactive pivot — Set is silent.
        if (extIdx == i)
            _value.Set(extIdx, extPrice);
        else
            _value.Revise(extIdx, extPrice);

        _bootstrapAtrs = null; // no longer needed once direction is declared
    }

    private void TrackBootstrapExtremes(Int64Bar bar)
    {
        if (bar.High > _startHigh)
            _startHigh = bar.High;

        if (bar.Low < _startLow)
            _startLow = bar.Low;
    }

    private void UpdateAtr(Int64Bar bar, int i)
    {
        double tr;
        if (i == 0)
        {
            tr = bar.High - bar.Low;
        }
        else
        {
            var highLow = (double)(bar.High - bar.Low);
            var highClose = Math.Abs((double)(bar.High - _previousClose));
            var lowClose = Math.Abs((double)(bar.Low - _previousClose));
            tr = Math.Max(highLow, Math.Max(highClose, lowClose));
        }

        _previousClose = bar.Close;

        if (i < _atrPeriod - 1)
        {
            _trSeedSum += tr;
        }
        else if (i == _atrPeriod - 1)
        {
            // Wilder seed: SMA of the first atrPeriod true ranges
            _atr = (_trSeedSum + tr) / _atrPeriod;
        }
        else
        {
            _atr += (tr - _atr) / _atrPeriod;
        }
    }
}
