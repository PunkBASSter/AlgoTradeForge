using AlgoTradeForge.Application;
using AlgoTradeForge.Application.Backtests;
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
            var trial = cache.Trials[t];
            writer.Write(trial.TimelineIndex);

            var pnl = trial.PnlDeltas;
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
    /// Falls back to trade P&amp;L when equity curves are empty.
    /// </summary>
    public void WriteDirect(IReadOnlyList<BacktestRunRecord> trials, string filePath)
    {
        if (trials.Count == 0)
            throw new ArgumentException("No trials provided.", nameof(trials));

        // Use trade P&L path when equity curves are not available
        if (trials[0].EquityCurve.Count == 0)
        {
            WriteDirectFromTradePnl(trials, filePath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        // Group by (canonical subscription key, barCount) → build timelines
        var timelineKeys = new Dictionary<(string PrimaryKey, int BarCount), int>();
        var timelines = new List<long[]>();
        var trialTimelineIndices = new int[trials.Count];

        for (var t = 0; t < trials.Count; t++)
        {
            var key = (BacktestInputsFormatter.Key(trials[t].DataSubscriptions[0]), trials[t].EquityCurve.Count);
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

            trialTimelineIndices[t] = tlIdx;
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
            writer.Write(trialTimelineIndices[t]);

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

    /// <summary>
    /// Writes trial data from trade P&L when equity curves are not available.
    /// Each trial gets its own timeline (trade timestamps).
    /// </summary>
    private void WriteDirectFromTradePnl(IReadOnlyList<BacktestRunRecord> trials, string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        using var writer = new BinaryWriter(fs);

        writer.Write(FormatVersion);

        // Each trial gets its own timeline
        writer.Write(trials.Count); // timelineCount = trialCount
        for (var t = 0; t < trials.Count; t++)
        {
            var trades = trials[t].TradePnl;
            writer.Write(trades.Count);
            for (var i = 0; i < trades.Count; i++)
                writer.Write(trades[i].TimestampMs);
        }

        writer.Write(trials.Count);
        for (var t = 0; t < trials.Count; t++)
        {
            writer.Write(t); // timelineIndex = trial index

            var trades = trials[t].TradePnl;
            for (var i = 0; i < trades.Count; i++)
                writer.Write(trades[i].Pnl);
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
        var trials = new TrialData[trialCount];

        for (var t = 0; t < trialCount; t++)
        {
            var tlIdx = reader.ReadInt32();
            var barCount = timelines[tlIdx].Length;

            var pnl = new double[barCount];
            for (var b = 0; b < barCount; b++)
                pnl[b] = reader.ReadDouble();
            trials[t] = new TrialData(tlIdx, pnl);
        }

        return new SimulationCache(timelines, trials);
    }
}
