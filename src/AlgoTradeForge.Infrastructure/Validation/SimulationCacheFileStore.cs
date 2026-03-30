using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Validation;
using AlgoTradeForge.Domain.Validation;

namespace AlgoTradeForge.Infrastructure.Validation;

/// <summary>
/// Binary file persistence for <see cref="SimulationCache"/>.
///
/// Binary format (all little-endian):
///   [int32 trialCount][int32 barCount]
///   [long[barCount] timestamps]
///   [double[trialCount * barCount] matrix — row-major, one row per trial]
/// </summary>
public sealed class SimulationCacheFileStore : ISimulationCacheFileStore
{
    /// <summary>Writes the cache to a binary file.</summary>
    public void Write(SimulationCache cache, string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        using var writer = new BinaryWriter(fs);

        writer.Write(cache.TrialCount);
        writer.Write(cache.BarCount);

        // Timestamps
        for (var i = 0; i < cache.BarCount; i++)
            writer.Write(cache.BarTimestamps[i]);

        // P&L matrix (row-major)
        for (var t = 0; t < cache.TrialCount; t++)
        {
            var row = cache.TrialPnlMatrix[t];
            for (var b = 0; b < cache.BarCount; b++)
                writer.Write(row[b]);
        }
    }

    /// <summary>Reads a binary cache file fully into memory.</summary>
    public SimulationCache Read(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        using var reader = new BinaryReader(fs);

        var trialCount = reader.ReadInt32();
        var barCount = reader.ReadInt32();

        var timestamps = new long[barCount];
        for (var i = 0; i < barCount; i++)
            timestamps[i] = reader.ReadInt64();

        var matrix = new double[trialCount][];
        for (var t = 0; t < trialCount; t++)
        {
            var row = new double[barCount];
            for (var b = 0; b < barCount; b++)
                row[b] = reader.ReadDouble();
            matrix[t] = row;
        }

        return new SimulationCache(timestamps, matrix);
    }

    /// <summary>
    /// Writes trial data directly to binary format, computing P&amp;L deltas on the fly.
    /// This avoids building an intermediate <see cref="SimulationCache"/> in memory.
    /// Delta logic mirrors <see cref="SimulationCacheBuilder.Build"/>.
    /// </summary>
    public void WriteDirect(IReadOnlyList<BacktestRunRecord> trials, string filePath)
    {
        if (trials.Count == 0)
            throw new ArgumentException("No trials provided.", nameof(trials));

        var firstCurve = trials[0].EquityCurve;
        if (firstCurve.Count == 0)
            throw new ArgumentException("Trial 0 has an empty equity curve.");

        var barCount = firstCurve.Count;
        var trialCount = trials.Count;

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        using var writer = new BinaryWriter(fs);

        writer.Write(trialCount);
        writer.Write(barCount);

        // Timestamps from first trial
        for (var i = 0; i < barCount; i++)
            writer.Write(firstCurve[i].TimestampMs);

        // P&L delta matrix — compute on the fly per trial
        for (var t = 0; t < trialCount; t++)
        {
            var curve = trials[t].EquityCurve;
            if (curve.Count != barCount)
                throw new ArgumentException(
                    $"Trial {t} has {curve.Count} equity points but expected {barCount}.");

            var initialCapital = (double)trials[t].Metrics.InitialCapital;
            writer.Write(curve[0].Value - initialCapital);
            for (var i = 1; i < barCount; i++)
                writer.Write(curve[i].Value - curve[i - 1].Value);
        }
    }
}