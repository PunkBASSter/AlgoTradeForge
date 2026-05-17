using System.Globalization;
using AlgoTradeForge.Application.IO;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage.Buffered;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Storage;

internal sealed class FeedCsvWriter : BufferedPartitionWriter, IFeedWriter
{
    public FeedCsvWriter(
        IFileStorage storage,
        IPartitionTailIndex tailIndex,
        IOptions<HistoryLoaderStorageOptions> options,
        ILogger<FeedCsvWriter> logger,
        WriteLockManager locks)
        : base(storage, tailIndex, options, logger, locks)
    {
    }

    public void Write(string assetDir, string feedName, string interval, string[] columns, FeedRecord record)
    {
        var partitionKey = GetPartitionKey(assetDir, feedName, interval, record.TimestampMs);
        var header = $"ts,{string.Join(',', columns)}";
        var valuesPart = string.Join(',', record.Values.Select(v => v.ToString(CultureInfo.InvariantCulture)));
        var row = $"{record.TimestampMs},{valuesPart}";
        AppendRow(partitionKey, header, row, record.TimestampMs);
    }

    public async Task<long?> ResumeFrom(string assetDir, string feedName, string interval, CancellationToken ct = default)
    {
        // PR3: absolute paths until PR4's StorageKeys migration.
        var feedDir = Path.Combine(assetDir, feedName);
        if (!Directory.Exists(feedDir)) return null;

        var pattern = string.IsNullOrEmpty(interval) ? "????-??.csv" : $"????-??_{interval}.csv";
        var files = Directory.GetFiles(feedDir, pattern).OrderByDescending(f => f).ToArray();
        if (files.Length == 0) return null;

        var latestFile = files[0];
        var ts = await TailIndex.GetLastTimestamp(latestFile, ct);
        if (!ts.HasValue) return null;

        // Resume only sets the watermark; the schema is unknown here. The first AppendRow
        // installs the real header (the base overwrites the empty placeholder).
        RegisterPartitionWatermark(latestFile, header: "", ts.Value);
        return ts.Value;
    }

    private static string GetPartitionKey(string assetDir, string feedName, string interval, long timestampMs)
    {
        var partitionDate = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime;
        var fileName = string.IsNullOrEmpty(interval)
            ? $"{partitionDate:yyyy-MM}.csv"
            : $"{partitionDate:yyyy-MM}_{interval}.csv";
        return Path.Combine(assetDir, feedName, fileName);
    }
}
