using AlgoTradeForge.Storage;
using AlgoTradeForge.Domain;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage.Buffered;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Storage;

internal sealed class CandleCsvWriter : BufferedPartitionWriter, ICandleWriter
{
    private const string CandleHeader = "ts,o,h,l,c,vol";

    public CandleCsvWriter(
        IFileStorage storage,
        IPartitionTailIndex tailIndex,
        IOptions<HistoryLoaderStorageOptions> options,
        ILogger<CandleCsvWriter> logger,
        WriteLockManager locks)
        : base(storage, tailIndex, options, logger, locks)
    {
    }

    public void Write(string assetDir, string interval, CandleRecord record, int decimalDigits)
    {
        var partitionKey = GetPartitionKey(assetDir, interval, record.TimestampMs);

        // Scale by 10^decimalDigits so the reader (PartitionedCsvBarLoader) can reconstruct
        // the original decimal values using the same multiplier stored in feeds.json.
        var multiplier = (decimal)Math.Pow(10, decimalDigits);
        var open   = MoneyConvert.ToLong(record.Open   * multiplier);
        var high   = MoneyConvert.ToLong(record.High   * multiplier);
        var low    = MoneyConvert.ToLong(record.Low    * multiplier);
        var close  = MoneyConvert.ToLong(record.Close  * multiplier);
        var volume = MoneyConvert.ToLong(record.Volume * multiplier);

        var row = $"{record.TimestampMs},{open},{high},{low},{close},{volume}";
        AppendRow(partitionKey, CandleHeader, row, record.TimestampMs);
    }

    public async Task<long?> ResumeFrom(string assetDir, string interval, CancellationToken ct = default)
    {
        // PR3: partitions are addressed by absolute path (matching PR2's hand-built loader path).
        // PR4 migrates the Write path to StorageKeys.CandlePartition.
        var candlesDir = Path.Combine(assetDir, "candles");
        var pattern = $"*_{interval}.csv";
        var files = await ListPartitionFilesDescending(candlesDir, pattern, ct);
        if (files.Count == 0) return null;

        foreach (var file in files)
        {
            var ts = await TailIndex.GetLastTimestamp(file, ct);
            if (ts.HasValue)
            {
                RegisterPartitionWatermark(file, CandleHeader, ts.Value);
                return ts.Value;
            }
        }
        return null;
    }

    private static string GetPartitionKey(string assetDir, string interval, long timestampMs)
    {
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs);
        var partition = dt.UtcDateTime.ToString("yyyy-MM");
        return Path.Combine(assetDir, "candles", $"{partition}_{interval}.csv");
    }
}
