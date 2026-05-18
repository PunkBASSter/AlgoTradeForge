using AlgoTradeForge.Application;
using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.IO;
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
public sealed class SimulationCacheFileStore(IFileStorage fileStorage) : ISimulationCacheFileStore
{
    private const int FormatVersion = 3;

    public async Task Write(SimulationCache cache, string filePath, CancellationToken ct = default)
    {
        await using var session = await fileStorage.OpenWriteSession(filePath, ct);
        using (var writer = new BinaryWriter(session.Stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(FormatVersion);

            writer.Write(cache.TimelineCount);
            for (var tl = 0; tl < cache.TimelineCount; tl++)
            {
                var ts = cache.Timelines[tl];
                writer.Write(ts.Length);
                for (var b = 0; b < ts.Length; b++)
                    writer.Write(ts[b]);
            }

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
        await session.Commit(ct);
    }

    public async Task<SimulationCache> Read(string filePath, CancellationToken ct = default)
    {
        await using var stream = await fileStorage.OpenRead(filePath, ct);
        using var reader = new BinaryReader(stream);

        var version = reader.ReadInt32();
        if (version != FormatVersion)
            throw new InvalidDataException(
                $"Unsupported SimulationCache binary format version {version} (expected {FormatVersion}).");

        return ReadCore(reader);
    }

    public async Task WriteDirect(IReadOnlyList<BacktestRunRecord> trials, string filePath, CancellationToken ct = default)
    {
        if (trials.Count == 0)
            throw new ArgumentException("No trials provided.", nameof(trials));

        if (trials[0].EquityCurve.Count == 0)
        {
            await WriteDirectFromTradePnl(trials, filePath, ct);
            return;
        }

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

        await using var session = await fileStorage.OpenWriteSession(filePath, ct);
        using (var writer = new BinaryWriter(session.Stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(FormatVersion);

            writer.Write(timelines.Count);
            foreach (var ts in timelines)
            {
                writer.Write(ts.Length);
                for (var b = 0; b < ts.Length; b++)
                    writer.Write(ts[b]);
            }

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
        await session.Commit(ct);
    }

    private async Task WriteDirectFromTradePnl(IReadOnlyList<BacktestRunRecord> trials, string filePath, CancellationToken ct)
    {
        await using var session = await fileStorage.OpenWriteSession(filePath, ct);
        using (var writer = new BinaryWriter(session.Stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(FormatVersion);

            writer.Write(trials.Count);
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
                writer.Write(t);

                var trades = trials[t].TradePnl;
                for (var i = 0; i < trades.Count; i++)
                    writer.Write(trades[i].Pnl);
            }
        }
        await session.Commit(ct);
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
