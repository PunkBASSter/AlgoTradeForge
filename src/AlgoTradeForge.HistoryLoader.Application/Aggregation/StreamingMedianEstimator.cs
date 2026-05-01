namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Single-pass online median estimator using the P² algorithm (Jain &amp; Chlamtac, 1985)
/// with 5 markers at quantile positions <c>{0, 0.25, 0.5, 0.75, 1.0}</c>. State footprint is
/// ~120 bytes regardless of sample count — replaces the unbounded <c>List&lt;long&gt;</c>
/// median path in <see cref="AggregationPipeline"/> for tick-source jobs (TRD §6.4 fidelity
/// block; preserves the time-bar exact-median path for backwards-compat with Phase 1b
/// manifests).
/// </summary>
/// <remarks>
/// <para>
/// Accuracy at <c>p=0.5</c> is typically within 1% of the true sample median for unimodal
/// distributions. The manifest consumer uses the median to compute
/// <c>n_factor = threshold / median</c>, itself a fidelity *estimate*, so an approximate
/// median is acceptable.
/// </para>
/// <para>
/// Below 5 samples the estimator falls back to an exact sort-and-pick on the buffered points;
/// at 5+ samples the markers are seeded and the streaming update runs.
/// </para>
/// </remarks>
public sealed class StreamingMedianEstimator
{
    private const double P = 0.5;

    private readonly double[] _q = new double[5];   // marker heights
    private readonly double[] _n = new double[5];   // marker positions (1-indexed by paper)
    private readonly double[] _np = new double[5];  // desired marker positions
    private readonly double[] _dn = new double[5];  // desired position increments per sample
    private long _count;

    public long Count => _count;

    public void Add(long sample) => Add((double)sample);

    public void Add(double sample)
    {
        if (_count < 5)
        {
            _q[_count] = sample;
            _count++;
            if (_count == 5)
                Initialize();
            return;
        }

        // 1. Find cell k and update boundary markers if sample is outside [q0, q4].
        int k;
        if (sample < _q[0]) { _q[0] = sample; k = 0; }
        else if (sample < _q[1]) k = 0;
        else if (sample < _q[2]) k = 1;
        else if (sample < _q[3]) k = 2;
        else if (sample <= _q[4]) k = 3;
        else { _q[4] = sample; k = 3; }

        // 2. Increment positions of markers above (or equal to) k+1.
        for (int i = k + 1; i < 5; i++)
            _n[i]++;

        // 3. Update desired positions.
        for (int i = 0; i < 5; i++)
            _np[i] += _dn[i];

        // 4. Adjust heights of inner markers (i = 1, 2, 3) that are off by ≥1 position.
        for (int i = 1; i <= 3; i++)
        {
            double d = _np[i] - _n[i];
            if ((d >= 1 && _n[i + 1] - _n[i] > 1)
                || (d <= -1 && _n[i - 1] - _n[i] < -1))
            {
                int sign = d >= 0 ? 1 : -1;
                double qPrime = Parabolic(i, sign);
                if (_q[i - 1] < qPrime && qPrime < _q[i + 1])
                    _q[i] = qPrime;
                else
                    _q[i] = Linear(i, sign);
                _n[i] += sign;
            }
        }

        _count++;
    }

    public double Median
    {
        get
        {
            if (_count == 0) return 0d;
            if (_count < 5)
            {
                var span = _q.AsSpan(0, (int)_count).ToArray();
                Array.Sort(span);
                int n = span.Length;
                return n % 2 == 1
                    ? span[n / 2]
                    : (span[n / 2 - 1] + span[n / 2]) / 2d;
            }
            return _q[2];
        }
    }

    private void Initialize()
    {
        // Sort the seed samples — they become the initial heights of the 5 markers.
        Array.Sort(_q);
        for (int i = 0; i < 5; i++)
            _n[i] = i + 1;

        _np[0] = 1;
        _np[1] = 1 + 2 * P;
        _np[2] = 1 + 4 * P;
        _np[3] = 3 + 2 * P;
        _np[4] = 5;

        _dn[0] = 0;
        _dn[1] = P / 2;
        _dn[2] = P;
        _dn[3] = (1 + P) / 2;
        _dn[4] = 1;
    }

    private double Parabolic(int i, int d) =>
        _q[i] + d / (_n[i + 1] - _n[i - 1]) *
            ((_n[i] - _n[i - 1] + d) * (_q[i + 1] - _q[i]) / (_n[i + 1] - _n[i])
             + (_n[i + 1] - _n[i] - d) * (_q[i] - _q[i - 1]) / (_n[i] - _n[i - 1]));

    private double Linear(int i, int d) =>
        _q[i] + d * (_q[i + d] - _q[i]) / (_n[i + d] - _n[i]);
}
