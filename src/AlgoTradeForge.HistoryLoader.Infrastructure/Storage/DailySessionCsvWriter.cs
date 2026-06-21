using System.Globalization;
using AlgoTradeForge.Storage;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage.Buffered;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Storage;

internal sealed class DailySessionCsvWriter : BufferedPartitionWriter, ISessionFeedWriter
{
    private const int ValueCount = 1; // [kind]
    private const string Header = "ts,kind";

    public DailySessionCsvWriter(
        IFileStorage storage,
        IPartitionTailIndex tailIndex,
        IOptions<HistoryLoaderStorageOptions> options,
        ILogger<DailySessionCsvWriter> logger,
        WriteLockManager locks)
        : base(storage, tailIndex, options, logger, locks)
    {
    }

    public void Write(string venueDir, FeedRecord record)
    {
        if (record.Values.Length != ValueCount)
            throw new ArgumentException(
                $"Session FeedRecord must have {ValueCount} value [kind]; got {record.Values.Length}.",
                nameof(record));

        var partitionKey = GetPartitionKey(venueDir, record.TimestampMs);
        var row =
            $"{record.TimestampMs.ToString(CultureInfo.InvariantCulture)}," +
            $"{((int)record.Values[0]).ToString(CultureInfo.InvariantCulture)}";

        // Dedup watermark is `ts`. Assumes no two _session events share a millisecond.
        // LIMITATION: two distinct-kind events in the same ms (e.g. ConnectorRestart + Heartbeat)
        // → the second is silently dropped. Lossless dedup deferred to Plan 3 (SessionEvent has no unique key; Sequence is always 0).
        AppendRow(partitionKey, Header, row, record.TimestampMs);
    }

    public async Task<SessionResumeState?> ResumeFrom(string venueDir, CancellationToken ct = default)
    {
        var feedDir = Path.Combine(venueDir, FeedNames.Session);
        var files = await ListPartitionFilesDescending(feedDir, "????-??-??.csv", ct);
        if (files.Count == 0) return null;

        var latestFile = files[0];
        var lastLine = await TailIndex.GetLastLine(latestFile, ct);
        if (lastLine is null
            || lastLine.StartsWith("ts,", StringComparison.Ordinal)
            || lastLine.Equals("ts", StringComparison.OrdinalIgnoreCase))
            return null;

        var parts = lastLine.Split(',');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts))
            return null;

        RegisterPartitionWatermark(latestFile, Header, ts);
        return new SessionResumeState(ts);
    }

    private static string GetPartitionKey(string venueDir, long timestampMs)
    {
        var dayKey = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs)
            .UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Path.Combine(venueDir, FeedNames.Session, $"{dayKey}.csv");
    }
}
