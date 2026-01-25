using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace AlgoTradeForge.Domain.History;

/// <summary>
/// Reads OHLCV bar data from local CSV files.
/// Supports flexible CSV formats with configurable column mappings.
/// </summary>
public sealed class CsvBarSource : IBarSource
{
    private readonly string? _basePath;
    private readonly CsvBarSourceOptions _options;

    /// <summary>
    /// Creates a CSV bar source that reads from the specified base path.
    /// </summary>
    /// <param name="basePath">
    /// Base directory for CSV files. If null, uses <see cref="HistoryContext.BasePath"/>.
    /// </param>
    /// <param name="options">CSV parsing options. If null, uses defaults.</param>
    public CsvBarSource(string? basePath = null, CsvBarSourceOptions? options = null)
    {
        _basePath = basePath;
        _options = options ?? CsvBarSourceOptions.Default;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
        string assetName,
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
                yield return bar;
            }
        }
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

/// <summary>
/// Configuration options for CSV parsing.
/// </summary>
public sealed record CsvBarSourceOptions
{
    /// <summary>
    /// Default options: comma-delimited, with header, columns in order: timestamp, open, high, low, close, volume.
    /// </summary>
    public static CsvBarSourceOptions Default { get; } = new();

    /// <summary>
    /// Column delimiter character.
    /// </summary>
    public char Delimiter { get; init; } = ',';

    /// <summary>
    /// Whether the CSV has a header row to skip.
    /// </summary>
    public bool HasHeader { get; init; } = true;

    /// <summary>
    /// File encoding. Defaults to UTF-8.
    /// </summary>
    public Encoding Encoding { get; init; } = Encoding.UTF8;

    /// <summary>
    /// Timestamp column index (0-based).
    /// </summary>
    public int TimestampColumn { get; init; } = 0;

    /// <summary>
    /// Open price column index (0-based).
    /// </summary>
    public int OpenColumn { get; init; } = 1;

    /// <summary>
    /// High price column index (0-based).
    /// </summary>
    public int HighColumn { get; init; } = 2;

    /// <summary>
    /// Low price column index (0-based).
    /// </summary>
    public int LowColumn { get; init; } = 3;

    /// <summary>
    /// Close price column index (0-based).
    /// </summary>
    public int CloseColumn { get; init; } = 4;

    /// <summary>
    /// Volume column index (0-based).
    /// </summary>
    public int VolumeColumn { get; init; } = 5;

    /// <summary>
    /// Timestamp format string for parsing. If null, auto-detects Unix or ISO formats.
    /// </summary>
    public string? TimestampFormat { get; init; }

    /// <summary>
    /// Custom function to resolve file name from asset name.
    /// If null, uses "{assetName}.csv".
    /// </summary>
    public Func<string, string>? FileNameResolver { get; init; }

    /// <summary>
    /// Creates options for Yahoo Finance CSV format.
    /// Columns: Date, Open, High, Low, Close, Adj Close, Volume
    /// </summary>
    public static CsvBarSourceOptions YahooFinance { get; } = new()
    {
        TimestampFormat = "yyyy-MM-dd",
        VolumeColumn = 6  // Skip Adj Close column
    };

    /// <summary>
    /// Creates options for Binance CSV format.
    /// Columns: Open time, Open, High, Low, Close, Volume, ...
    /// </summary>
    public static CsvBarSourceOptions Binance { get; } = new()
    {
        HasHeader = false
    };
}
