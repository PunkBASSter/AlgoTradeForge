using System.Globalization;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Benchmarks.Loaders;

/// <summary>
/// Loads the bundled BTCUSDT 1h CSV snapshot (2020-01 → 2024-12) into a single
/// <see cref="TimeSeries{T}"/>. Values are stored already scaled (×100); we keep
/// them as-is because the engine operates on <c>long</c> ticks throughout.
/// </summary>
public static class BundledCandleLoader
{
    private const string DataSubdir = "data/BTCUSDT_1h";

    // 60 months × ~720 bars/month ≈ 43,200 nominal; allow ±10% for monthly drift,
    // exchange downtime, leap-day variance.
    private const int MinExpectedBarCount = 38_000;
    private const int MaxExpectedBarCount = 48_000;
    private const long ExpectedHourMs = 60L * 60L * 1000L;

    public static TimeSeries<Int64Bar> LoadBtcUsdt1hFiveYears()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, DataSubdir);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException(
                $"Bundled candle directory not found: {dir}. Ensure data/**/*.csv is copied to output.");

        var files = Directory.GetFiles(dir, "*_1h.csv");
        Array.Sort(files, StringComparer.Ordinal);

        if (files.Length == 0)
            throw new InvalidOperationException($"No *_1h.csv files found under {dir}.");

        // 60 months × ~720 bars each ≈ 43,800 bars total
        var series = new TimeSeries<Int64Bar>(initialCapacity: 45_000);

        long previousTs = -1;
        foreach (var file in files)
        {
            using var reader = new StreamReader(file);
            // Skip header
            _ = reader.ReadLine();

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0) continue;
                var bar = ParseCsvRow(line);
                // Cheap monotonic check — an out-of-order row would skew benchmark
                // semantics (TradeRegistry walks bars assuming monotonic time) and
                // hint that the CSVs were re-exported in a different order.
                if (previousTs > 0 && bar.TimestampMs <= previousTs)
                    throw new InvalidDataException(
                        $"Bundled CSV is not monotonic: ts {bar.TimestampMs} follows {previousTs} in {Path.GetFileName(file)}.");
                previousTs = bar.TimestampMs;
                series.Add(bar);
            }
        }

        if (series.Count is < MinExpectedBarCount or > MaxExpectedBarCount)
            throw new InvalidDataException(
                $"Bundled CSV bar count {series.Count} outside expected range " +
                $"[{MinExpectedBarCount}, {MaxExpectedBarCount}]. CSV format may have changed " +
                $"(refresh from %LOCALAPPDATA%\\AlgoTradeForge\\History\\binance\\BTCUSDT\\candles\\).");

        // Spot-check median bar spacing — guards against accidental mixing of timeframes
        // (e.g. someone drops 1m CSVs into the 1h directory).
        var midSpan = series[series.Count / 2].TimestampMs - series[(series.Count / 2) - 1].TimestampMs;
        if (midSpan != ExpectedHourMs)
            throw new InvalidDataException(
                $"Bundled CSV median bar spacing {midSpan} ms ≠ expected {ExpectedHourMs} ms (1h). " +
                $"Wrong timeframe in data/BTCUSDT_1h/?");

        return series;
    }

    private static Int64Bar ParseCsvRow(string line)
    {
        // ts,o,h,l,c,vol — all int64
        var span = line.AsSpan();

        var ts = ParseLong(ref span);
        var o = ParseLong(ref span);
        var h = ParseLong(ref span);
        var l = ParseLong(ref span);
        var c = ParseLong(ref span);
        var v = ParseLong(ref span);

        return new Int64Bar(ts, o, h, l, c, v);
    }

    private static long ParseLong(ref ReadOnlySpan<char> remaining)
    {
        var comma = remaining.IndexOf(',');
        ReadOnlySpan<char> token;
        if (comma < 0)
        {
            token = remaining;
            remaining = default;
        }
        else
        {
            token = remaining.Slice(0, comma);
            remaining = remaining.Slice(comma + 1);
        }
        return long.Parse(token, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }
}
