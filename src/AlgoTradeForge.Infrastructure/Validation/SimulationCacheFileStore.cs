using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Validation;
using AlgoTradeForge.Domain.Validation;

namespace AlgoTradeForge.Infrastructure.Validation;

/// <summary>
/// Binary file persistence for <see cref="SimulationCache"/>.
///
/// Binary format v2 (all little-endian, per-trial timestamps):
///   [int32 version = 2]
///   [int32 trialCount]
///   For each trial:
///     [int32 barCount_t]
///     [long[barCount_t] timestamps]
///     [double[barCount_t] pnlDeltas]
/// </summary>
public sealed class SimulationCacheFileStore : ISimulationCacheFileStore
{
    private const int FormatVersion = 2;

    /// <summary>Writes the cache to a binary file.</summary>
    public void Write(SimulationCache cache, string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        using var writer = new BinaryWriter(fs);

        writer.Write(FormatVersion);
        writer.Write(cache.TrialCount);

        for (var t = 0; t < cache.TrialCount; t++)
        {
            var barCount = cache.GetBarCount(t);
            writer.Write(barCount);

            var timestamps = cache.TrialTimestamps[t];
            for (var b = 0; b < barCount; b++)
                writer.Write(timestamps[b]);

            var pnl = cache.TrialPnlMatrix[t];
            for (var b = 0; b < barCount; b++)
                writer.Write(pnl[b]);
        }
    }

    /// <summary>Reads a binary cache file fully into memory.</summary>
    public SimulationCache Read(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        using var reader = new BinaryReader(fs);

        var version = reader.ReadInt32();
        if (version != FormatVersion)
            throw new InvalidDataException(
                $"Unsupported SimulationCache binary format version {version} (expected {FormatVersion}).");

        var trialCount = reader.ReadInt32();

        var timestamps = new long[trialCount][];
        var matrix = new double[trialCount][];

        for (var t = 0; t < trialCount; t++)
        {
            var barCount = reader.ReadInt32();

            var ts = new long[barCount];
            for (var b = 0; b < barCount; b++)
                ts[b] = reader.ReadInt64();

            var pnl = new double[barCount];
            for (var b = 0; b < barCount; b++)
                pnl[b] = reader.ReadDouble();

            timestamps[t] = ts;
            matrix[t] = pnl;
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

        if (trials[0].EquityCurve.Count == 0)
            throw new ArgumentException("Trial 0 has an empty equity curve.");

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        using var writer = new BinaryWriter(fs);

        writer.Write(FormatVersion);
        writer.Write(trials.Count);

        for (var t = 0; t < trials.Count; t++)
        {
            var curve = trials[t].EquityCurve;
            var barCount = curve.Count;
            writer.Write(barCount);

            // Timestamps
            for (var i = 0; i < barCount; i++)
                writer.Write(curve[i].TimestampMs);

            // P&L deltas
            if (barCount > 0)
            {
                var initialCapital = (double)trials[t].Metrics.InitialCapital;
                writer.Write(curve[0].Value - initialCapital);
                for (var i = 1; i < barCount; i++)
                    writer.Write(curve[i].Value - curve[i - 1].Value);
            }
        }
    }
}
