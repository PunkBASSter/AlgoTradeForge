using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Indicators;

/// <summary>
/// Average Directional Index (ADX). Measures trend strength (0-100).
/// Uses standard ADX formula: DI+, DI-, DX, smoothed ADX.
/// Warmup period = 2 × period.
/// </summary>
public sealed class Adx : DoubleIndicatorBase
{
    private readonly int _period;
    private readonly IndicatorBuffer<double> _buffer = new("Value");
    private readonly Dictionary<string, IndicatorBuffer<double>> _buffers;

    private int _lastProcessedIndex = -1;
    private double _smoothedPlusDm;
    private double _smoothedMinusDm;
    private double _smoothedTr;
    private double _smoothedAdx;
    private int _warmupCount;
    private double _dxSum;
    private int _dxCount;
    private bool _diSmoothed;
    private bool _adxSmoothed;
    private long _prevHigh;
    private long _prevLow;
    private long _prevClose;

    public Adx(int period = 14)
    {
        _period = period;
        _buffers = new Dictionary<string, IndicatorBuffer<double>> { ["Value"] = _buffer };
        ApplyBufferCapacity();
    }

    public override IReadOnlyDictionary<string, IndicatorBuffer<double>> Buffers => _buffers;
    public override int MinimumHistory => _period * 2;

    public override void Compute(IReadOnlyList<Int64Bar> series)
    {
        for (var i = _buffer.Count; i < series.Count; i++)
            _buffer.Append(0.0);

        var startIndex = _lastProcessedIndex + 1;

        for (var i = startIndex; i < series.Count; i++)
        {
            var bar = series[i];

            if (i == 0)
            {
                _prevHigh = bar.High;
                _prevLow = bar.Low;
                _prevClose = bar.Close;
                _buffer.Set(i, 0.0);
                _lastProcessedIndex = i;
                continue;
            }

            // True Range
            var tr = (double)Math.Max(bar.High - bar.Low,
                Math.Max(Math.Abs(bar.High - _prevClose), Math.Abs(bar.Low - _prevClose)));

            // Directional Movement
            var upMove = (double)(bar.High - _prevHigh);
            var downMove = (double)(_prevLow - bar.Low);

            var plusDm = (upMove > downMove && upMove > 0) ? upMove : 0;
            var minusDm = (downMove > upMove && downMove > 0) ? downMove : 0;

            _prevHigh = bar.High;
            _prevLow = bar.Low;
            _prevClose = bar.Close;

            _warmupCount++;

            if (!_diSmoothed)
            {
                // Accumulate for first smoothing
                _smoothedPlusDm += plusDm;
                _smoothedMinusDm += minusDm;
                _smoothedTr += tr;

                if (_warmupCount >= _period)
                {
                    _diSmoothed = true;
                    // Compute first DX but don't add to sum — the !_adxSmoothed phase
                    // will collect exactly _period DX values starting from the next bar
                }

                _buffer.Set(i, 0.0);
            }
            else if (!_adxSmoothed)
            {
                // Wilder's smoothing for DI
                _smoothedPlusDm = _smoothedPlusDm - _smoothedPlusDm / _period + plusDm;
                _smoothedMinusDm = _smoothedMinusDm - _smoothedMinusDm / _period + minusDm;
                _smoothedTr = _smoothedTr - _smoothedTr / _period + tr;

                var plusDi = _smoothedTr > 0 ? 100 * _smoothedPlusDm / _smoothedTr : 0;
                var minusDi = _smoothedTr > 0 ? 100 * _smoothedMinusDm / _smoothedTr : 0;
                var diSum = plusDi + minusDi;
                var dx = diSum > 0 ? 100 * Math.Abs(plusDi - minusDi) / diSum : 0;

                _dxSum += dx;
                _dxCount++;

                if (_dxCount >= _period)
                {
                    _adxSmoothed = true;
                    _smoothedAdx = _dxSum / _period;
                    _buffer.Set(i, _smoothedAdx);
                }
                else
                {
                    _buffer.Set(i, 0.0);
                }
            }
            else
            {
                // Smoothed ADX
                _smoothedPlusDm = _smoothedPlusDm - _smoothedPlusDm / _period + plusDm;
                _smoothedMinusDm = _smoothedMinusDm - _smoothedMinusDm / _period + minusDm;
                _smoothedTr = _smoothedTr - _smoothedTr / _period + tr;

                var plusDi = _smoothedTr > 0 ? 100 * _smoothedPlusDm / _smoothedTr : 0;
                var minusDi = _smoothedTr > 0 ? 100 * _smoothedMinusDm / _smoothedTr : 0;
                var diSum = plusDi + minusDi;
                var dx = diSum > 0 ? 100 * Math.Abs(plusDi - minusDi) / diSum : 0;

                _smoothedAdx = (_smoothedAdx * (_period - 1) + dx) / _period;
                _buffer.Set(i, _smoothedAdx);
            }
        }

        if (series.Count > 0)
            _lastProcessedIndex = series.Count - 1;
    }
}
