using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace AlgoTradeForge.Domain.History;

internal readonly record struct BarCacheKey(
    string AssetName,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime);

public sealed class CsvBarSource : IBarSource
{
    private static readonly MemoryCache s_cache = new(new MemoryCacheOptions
    {
        SizeLimit = 1000
    });

    private readonly string? _basePath;
    private readonly CsvBarSourceOptions _options;

    public CsvBarSource(string? basePath = null, CsvBarSourceOptions? options = null)
    {
        _basePath = basePath;
        _options = options ?? CsvBarSourceOptions.Default;
    }

    public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
        string assetName,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var cacheKey = new BarCacheKey(assetName, startTime, endTime);

        if (s_cache.TryGetValue(cacheKey, out IReadOnlyList<OhlcvBar>? cachedBars))
        {
            foreach (var bar in cachedBars!)
            {
                ct.ThrowIfCancellationRequested();
                yield return bar;
            }
            yield break;
        }

        var bars = new List<OhlcvBar>();

        await foreach (var bar in LoadBarsFromFileAsync(assetName, startTime, endTime, ct))
        {
            bars.Add(bar);
        }

        var cacheEntryOptions = new MemoryCacheEntryOptions()
            .SetSize(1);
        s_cache.Set(cacheKey, (IReadOnlyList<OhlcvBar>)bars, cacheEntryOptions);

        foreach (var bar in bars)
        {
            yield return bar;
        }
    }

    private async IAsyncEnumerable<OhlcvBar> LoadBarsFromFileAsync(
        string assetName,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var basePath = _basePath ?? HistoryContext.BasePath
            ?? throw new InvalidOperationException(
                "History path not configured. Set CsvBarSource basePath or HistoryContext.BasePath.");

        var filePath = ResolveFilePath(basePath, assetName);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException(
                $"CSV history file not found for asset '{assetName}'.", filePath);
        }

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65536,
            useAsync: true);

        using var reader = new StreamReader(stream, _options.Encoding);

        if (_options.HasHeader)
        {
            await reader.ReadLineAsync(ct);
        }

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (TryParseLine(line, out var bar))
            {
                if (bar.Timestamp >= startTime && bar.Timestamp <= endTime)
                {
                    yield return bar;
                }
            }
        }
    }

    public static void ClearCache()
    {
        s_cache.Clear();
    }

    public static void InvalidateCache(string assetName, DateTimeOffset startTime, DateTimeOffset endTime)
    {
        var cacheKey = new BarCacheKey(assetName, startTime, endTime);
        s_cache.Remove(cacheKey);
    }

    private string ResolveFilePath(string basePath, string assetName)
    {
        var fileName = _options.FileNameResolver?.Invoke(assetName)
            ?? $"{assetName}.csv";

        return Path.Combine(basePath, fileName);
    }

    private bool TryParseLine(string line, out OhlcvBar bar)
    {
        bar = default;

        var columns = line.Split(_options.Delimiter);
        if (columns.Length < 6)
            return false;

        try
        {
            var timestamp = ParseTimestamp(columns[_options.TimestampColumn].Trim());
            var open = ParseDecimal(columns[_options.OpenColumn]);
            var high = ParseDecimal(columns[_options.HighColumn]);
            var low = ParseDecimal(columns[_options.LowColumn]);
            var close = ParseDecimal(columns[_options.CloseColumn]);
            var volume = ParseDecimal(columns[_options.VolumeColumn]);

            bar = new OhlcvBar(timestamp, open, high, low, close, volume);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private DateTimeOffset ParseTimestamp(string value)
    {
        if (_options.TimestampFormat is not null)
        {
            return DateTimeOffset.ParseExact(
                value,
                _options.TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal);
        }

        // Try Unix timestamp (seconds or milliseconds)
        if (long.TryParse(value, out var unixTime))
        {
            return unixTime > 9_999_999_999
                ? DateTimeOffset.FromUnixTimeMilliseconds(unixTime)
                : DateTimeOffset.FromUnixTimeSeconds(unixTime);
        }

        // Fall back to standard parsing
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    }

    private static decimal ParseDecimal(string value)
    {
        return decimal.Parse(value.Trim(), CultureInfo.InvariantCulture);
    }
}

public sealed record CsvBarSourceOptions
{
    public static CsvBarSourceOptions Default { get; } = new();

    public char Delimiter { get; init; } = ',';

    public bool HasHeader { get; init; } = true;

    public Encoding Encoding { get; init; } = Encoding.UTF8;

    public int TimestampColumn { get; init; } = 0;

    public int OpenColumn { get; init; } = 1;

    public int HighColumn { get; init; } = 2;

    public int LowColumn { get; init; } = 3;

    public int CloseColumn { get; init; } = 4;

    public int VolumeColumn { get; init; } = 5;

    public string? TimestampFormat { get; init; }

    public Func<string, string>? FileNameResolver { get; init; }

    public static CsvBarSourceOptions YahooFinance { get; } = new()
    {
        TimestampFormat = "yyyy-MM-dd",
        VolumeColumn = 6
    };

    public static CsvBarSourceOptions Binance { get; } = new()
    {
        HasHeader = false
    };
}
