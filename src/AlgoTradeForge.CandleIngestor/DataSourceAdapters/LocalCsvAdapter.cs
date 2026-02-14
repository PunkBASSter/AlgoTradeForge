using System.Globalization;
using System.Runtime.CompilerServices;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.CandleIngestor.DataSourceAdapters;

public sealed class LocalCsvAdapter(AdapterOptions options, ILogger<LocalCsvAdapter> logger) : IDataAdapter
{
    public async IAsyncEnumerable<RawCandle> FetchCandlesAsync(
        string symbol,
        TimeSpan interval,
        DateTimeOffset from,
        DateTimeOffset to,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var sourceDir = options.BaseUrl;
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"Local CSV source directory not found: {sourceDir}");

        var intervalStr = interval.ToString(@"hh\-mm\-ss");
        var pattern = $"*_{intervalStr}_{symbol}@*.csv";
        var files = Directory.GetFiles(sourceDir, pattern)
            .Order()
            .ToArray();

        if (files.Length == 0)
        {
            logger.LogWarning("NoFilesFound: pattern={Pattern} in {Directory}", pattern, sourceDir);
            yield break;
        }

        logger.LogInformation("LocalCsvFilesMatched: {Count} file(s) for {Symbol}, interval={Interval}",
            files.Length, symbol, intervalStr);

        foreach (var file in files)
        {
            logger.LogDebug("ReadingFile: {File}", file);

            using var reader = new StreamReader(file);

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var parts = line.Split(';');
                if (parts.Length < 7)
                    continue;

                var date = DateOnly.ParseExact(parts[0], "yyyyMMdd", CultureInfo.InvariantCulture);
                var time = TimeOnly.ParseExact(parts[1], "HH:mm:ss", CultureInfo.InvariantCulture);
                var timestamp = new DateTimeOffset(date.ToDateTime(time), TimeSpan.Zero);

                if (timestamp < from)
                    continue;
                if (timestamp >= to)
                    yield break;

                yield return new RawCandle(
                    timestamp,
                    decimal.Parse(parts[2], CultureInfo.InvariantCulture),
                    decimal.Parse(parts[3], CultureInfo.InvariantCulture),
                    decimal.Parse(parts[4], CultureInfo.InvariantCulture),
                    decimal.Parse(parts[5], CultureInfo.InvariantCulture),
                    decimal.Parse(parts[6], CultureInfo.InvariantCulture));
            }
        }
    }
}
