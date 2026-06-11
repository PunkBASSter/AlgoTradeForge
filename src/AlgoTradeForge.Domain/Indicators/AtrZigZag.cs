using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Indicators;

/// <summary>
/// Volatility-adaptive zigzag: reversal threshold = multiplier * Wilder ATR,
/// with the ATR value pinned at the bar of the current extremum.
/// Port of the ATR_MULTIPLE detector selected by the swing_prediction research
/// (AlgoTradeForge.Research swing_prediction/swing_lib/detector.py) — it showed
/// 2.5–3x more stable swing density than fixed-percent zigzags (CV of yearly
/// swing counts ~0.2 vs ~0.65 on BTCUSDT).
///
/// Intrabar convention is high-first: in the down phase an upward confirmation
/// against the prior extreme is checked BEFORE the bar's low may extend it.
///
/// Configuration (BTCUSDT, research-calibrated for ~100 swings/year):
/// keep the ATR window spanning ~1 day of wall time and scale the multiplier
/// by 1/sqrt(barMinutes):
///   1m  → atrPeriod 1440, multiplier 56–64
///   5m  → atrPeriod 288,  multiplier 25–29
///   15m → atrPeriod 96,   multiplier 14–17
///   1h  → atrPeriod 24,   multiplier 7.3–8.3
///   4h  → atrPeriod 14 (floor for ATR stability), multiplier ~3.5–4.2
///   1d  → atrPeriod 14,   multiplier ~1.7–2.0
/// The sqrt scaling is approximate; recalibrate the multiplier against a target
/// swing density for precise parity with the 1m research calibration.
/// </summary>
public sealed class AtrZigZag : Int64IndicatorBase
{
    private readonly double _multiplier;
    private readonly int _atrPeriod;

    private readonly IndicatorBuffer<long> _buffer = new("Value", skipDefaultValues: true);
    private readonly Dictionary<string, IndicatorBuffer<long>> _buffers;

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

    public AtrZigZag(double multiplier, int atrPeriod)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(multiplier);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(atrPeriod);

        _multiplier = multiplier;
        _atrPeriod = atrPeriod;
        _buffers = new Dictionary<string, IndicatorBuffer<long>> { ["Value"] = _buffer };
        ApplyBufferCapacity();
    }

    public override int MinimumHistory => _atrPeriod + 1;

    /// <inheritdoc cref="DeltaZigZag.CapacityLimit"/>
    public override int? CapacityLimit => 0;

    public override IReadOnlyDictionary<string, IndicatorBuffer<long>> Buffers => _buffers;

    public override void Compute(IReadOnlyList<Int64Bar> series)
    {
        for (var i = _buffer.Count; i < series.Count; i++)
            _buffer.Append(0L);

        var startIndex = _lastProcessedIndex + 1;

        for (var i = startIndex; i < series.Count; i++)
        {
            var bar = series[i];
            UpdateAtr(bar, i);
            _bootstrapAtrs?.Add(_atr);

            if (double.IsNaN(_atr))
            {
                // ATR warmup: only track running extremes for the bootstrap phase
                TrackBootstrapExtremes(bar);
                continue;
            }

            if (_direction == 0)
            {
                Bootstrap(series, bar, i);
                continue;
            }

            if (_direction > 0)
            {
                // Extend-first in the up phase (high-first intrabar convention)
                if (bar.High > _extPrice)
                {
                    if (_extIdx != i)
                        _buffer.Revise(_extIdx, 0L);

                    _extIdx = i;
                    _extPrice = bar.High;
                    _extAtr = _atr;
                    _buffer.Set(i, bar.High);
                }

                if ((double)(_extPrice - bar.Low) >= _multiplier * _extAtr && i - _extIdx >= 1)
                {
                    // Reversal down confirmed: the high pivot stands, start a new low
                    _direction = -1;
                    _extIdx = i;
                    _extPrice = bar.Low;
                    _extAtr = _atr;
                    _buffer.Set(i, bar.Low);
                }
            }
            else
            {
                // Confirm-first in the down phase: an upward reversal against the
                // prior extreme wins over extending the low on the same bar
                if ((double)(bar.High - _extPrice) >= _multiplier * _extAtr && i - _extIdx >= 1)
                {
                    _direction = 1;
                    _extIdx = i;
                    _extPrice = bar.High;
                    _extAtr = _atr;
                    _buffer.Set(i, bar.High);
                }
                else if (bar.Low < _extPrice)
                {
                    if (_extIdx != i)
                        _buffer.Revise(_extIdx, 0L);

                    _extIdx = i;
                    _extPrice = bar.Low;
                    _extAtr = _atr;
                    _buffer.Set(i, bar.Low);
                }
            }
        }

        if (series.Count > 0)
            _lastProcessedIndex = series.Count - 1;
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
            _buffer.Set(extIdx, extPrice);
        else
            _buffer.Revise(extIdx, extPrice);

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
