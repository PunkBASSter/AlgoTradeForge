using AlgoTradeForge.Application;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Validation;
using AlgoTradeForge.Domain.Validation;

namespace AlgoTradeForge.Infrastructure.Validation;

/// <summary>
/// Binary file persistence for <see cref="SimulationCache"/>.
///
/// Binary format (all little-endian, deduplicated timelines):
///   [int32 version = 3]
///   [int32 timelineCount]
///   For each timeline:
///     [int32 barCount]
///     [long[barCount] timestamps]
///   [int32 trialCount]
///   For each trial:
///     [int32 timelineIndex]
///     [double[barCount] pnlDeltas]   // barCount from Timelines[timelineIndex]
/// </summary>
public sealed class SimulationCacheFileStore : ISimulationCacheFileStore
{
    private const int FormatVersion = 3;

    /// <summary>Writes the cache to a binary file.</summary>
    public void Write(SimulationCache cache, string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        using var writer = new BinaryWriter(fs);

        writer.Write(FormatVersion);

        // Timelines
        writer.Write(cache.TimelineCount);
        for (var tl = 0; tl < cache.TimelineCount; tl++)
        {
            var ts = cache.Timelines[tl];
            writer.Write(ts.Length);
            for (var b = 0; b < ts.Length; b++)
                writer.Write(ts[b]);
        }

        // Trials
        writer.Write(cache.TrialCount);
        for (var t = 0; t < cache.TrialCount; t++)
        {
            writer.Write(cache.TrialTimelineIndex[t]);

            var pnl = cache.TrialPnlMatrix[t];
            for (var b = 0; b < pnl.Length; b++)
                writer.Write(pnl[b]);
        }
    }

    /// <summary>Reads a binary cache file.</summary>
    public SimulationCache Read(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        using var reader = new BinaryReader(fs);

        var version = reader.ReadInt32();
        if (version != FormatVersion)
            throw new InvalidDataException(
                $"Unsupported SimulationCache binary format version {version} (expected {FormatVersion}).");

        return ReadCore(reader);
    }

    /// <summary>
    /// Writes trial data directly to binary format, computing P&amp;L deltas on the fly.
    /// Groups trials by <see cref="DataSubscriptionDto"/> for timeline deduplication.
    /// </summary>
    public void WriteDirect(IReadOnlyList<BacktestRunRecord> trials, string filePath)
    {
        if (trials.Count == 0)
            throw new ArgumentException("No trials provided.", nameof(trials));

        if (trials[0].EquityCurve.Count == 0)
            throw new ArgumentException("Trial 0 has an empty equity curve.");

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        // Group by (subscription, barCount) → build timelines
        var timelineKeys = new Dictionary<(DataSubscriptionDto, int), int>();
        var timelines = new List<long[]>();
        var trialTimelineIndex = new int[trials.Count];

        for (var t = 0; t < trials.Count; t++)
        {
            var key = (trials[t].DataSubscription, trials[t].EquityCurve.Count);
            if (!timelineKeys.TryGetValue(key, out var tlIdx))
            {
                tlIdx = timelines.Count;
                timelineKeys[key] = tlIdx;
                var curve = trials[t].EquityCurve;
                var ts = new long[curve.Count];
                for (var i = 0; i < curve.Count; i++)
                    ts[i] = curve[i].TimestampMs;
                timelines.Add(ts);
            }

            trialTimelineIndex[t] = tlIdx;
        }

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        using var writer = new BinaryWriter(fs);

        writer.Write(FormatVersion);

        // Write timelines
        writer.Write(timelines.Count);
        foreach (var ts in timelines)
        {
            writer.Write(ts.Length);
            for (var b = 0; b < ts.Length; b++)
                writer.Write(ts[b]);
        }

        // Write trials (timeline index + PnL deltas)
        writer.Write(trials.Count);
        for (var t = 0; t < trials.Count; t++)
        {
            writer.Write(trialTimelineIndex[t]);

            var curve = trials[t].EquityCurve;
            if (curve.Count > 0)
            {
                var initialCapital = (double)trials[t].Metrics.InitialCapital;
                writer.Write(curve[0].Value - initialCapital);
                for (var i = 1; i < curve.Count; i++)
                    writer.Write(curve[i].Value - curve[i - 1].Value);
            }
        }
    }

    private static SimulationCache ReadCore(BinaryReader reader)
    {
        var timelineCount = reader.ReadInt32();
        var timelines = new long[timelineCount][];
        for (var tl = 0; tl < timelineCount; tl++)
        {
            var barCount = reader.ReadInt32();
            var ts = new long[barCount];
            for (var b = 0; b < barCount; b++)
                ts[b] = reader.ReadInt64();
            timelines[tl] = ts;
        }

        var trialCount = reader.ReadInt32();
        var trialTimelineIndex = new int[trialCount];
        var matrix = new double[trialCount][];

        for (var t = 0; t < trialCount; t++)
        {
            trialTimelineIndex[t] = reader.ReadInt32();
            var barCount = timelines[trialTimelineIndex[t]].Length;

            var pnl = new double[barCount];
            for (var b = 0; b < barCount; b++)
                pnl[b] = reader.ReadDouble();
            matrix[t] = pnl;
        }

        return new SimulationCache(timelines, trialTimelineIndex, matrix);
    }
}
