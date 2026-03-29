namespace AlgoTradeForge.Domain.Validation;

/// <summary>
/// Stores the full N×T P&amp;L matrix from optimization trials.
/// Row = trial, Column = bar. Values are per-bar equity deltas.
/// </summary>
public sealed class SimulationCache
{
    public long[] BarTimestamps { get; }
    public double[][] TrialPnlMatrix { get; }
    public int TrialCount { get; }
    public int BarCount { get; }

    public SimulationCache(long[] barTimestamps, double[][] trialPnlMatrix)
    {
        ArgumentNullException.ThrowIfNull(barTimestamps);
        ArgumentNullException.ThrowIfNull(trialPnlMatrix);

        var t = barTimestamps.Length;
        for (var i = 0; i < trialPnlMatrix.Length; i++)
        {
            if (trialPnlMatrix[i].Length != t)
                throw new ArgumentException(
                    $"Trial {i} has {trialPnlMatrix[i].Length} bars but expected {t}.");
        }

        BarTimestamps = barTimestamps;
        TrialPnlMatrix = trialPnlMatrix;
        TrialCount = trialPnlMatrix.Length;
        BarCount = t;
    }

    /// <summary>Returns the P&amp;L row for a single trial as a span (zero-allocation).</summary>
    public ReadOnlySpan<double> GetTrialPnl(int trialIndex) => TrialPnlMatrix[trialIndex];

    /// <summary>Returns a sub-window of a trial's P&amp;L as a span (zero-allocation, zero-copy).</summary>
    public ReadOnlySpan<double> GetTrialPnlWindow(int trialIndex, int startBar, int length) =>
        TrialPnlMatrix[trialIndex].AsSpan(startBar, length);

    /// <summary>Returns a cross-sectional column slice (all trials at one bar). Allocates a new array.</summary>
    public double[] GetBarPnl(int barIndex)
    {
        var result = new double[TrialCount];
        for (var i = 0; i < TrialCount; i++)
            result[i] = TrialPnlMatrix[i][barIndex];
        return result;
    }

    /// <summary>Creates a new cache with a subset of bars [startBar, endBar).</summary>
    public SimulationCache SliceWindow(int startBar, int endBar)
    {
        if (startBar < 0 || endBar > BarCount || startBar >= endBar)
            throw new ArgumentOutOfRangeException(
                nameof(startBar), $"Invalid slice [{startBar}, {endBar}) for BarCount={BarCount}.");

        var length = endBar - startBar;
        var timestamps = BarTimestamps.AsSpan(startBar, length).ToArray();
        var matrix = new double[TrialCount][];
        for (var i = 0; i < TrialCount; i++)
            matrix[i] = TrialPnlMatrix[i].AsSpan(startBar, length).ToArray();

        return new SimulationCache(timestamps, matrix);
    }

    /// <summary>Computes cumulative equity curve for a trial: running sum of P&amp;L deltas + initial equity.</summary>
    public double[] ComputeCumulativeEquity(int trialIndex, double initialEquity)
    {
        var pnl = TrialPnlMatrix[trialIndex];
        var equity = new double[pnl.Length];
        var cumulative = initialEquity;
        for (var i = 0; i < pnl.Length; i++)
        {
            cumulative += pnl[i];
            equity[i] = cumulative;
        }

        return equity;
    }
}
