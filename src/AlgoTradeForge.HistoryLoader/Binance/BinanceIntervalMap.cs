namespace AlgoTradeForge.HistoryLoader.Binance;

/// <summary>
/// Bidirectional mapping between <see cref="TimeSpan"/> values and Binance kline interval strings.
/// </summary>
public static class BinanceIntervalMap
{
    /// <summary>
    /// Maps a <see cref="TimeSpan"/> to the corresponding Binance interval string (e.g. "1m", "4h", "1d").
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the interval is not a supported Binance interval.</exception>
    public static string ToIntervalString(TimeSpan interval) => interval.TotalMinutes switch
    {
        1    => "1m",
        3    => "3m",
        5    => "5m",
        15   => "15m",
        30   => "30m",
        60   => "1h",
        120  => "2h",
        240  => "4h",
        360  => "6h",
        480  => "8h",
        720  => "12h",
        1440 => "1d",
        _    => throw new ArgumentException($"Unsupported interval: {interval}", nameof(interval))
    };

    /// <summary>
    /// Parses a Binance interval string (e.g. "1m", "4h", "1d") back to a <see cref="TimeSpan"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the string is not a recognised Binance interval.</exception>
    public static TimeSpan ToTimeSpan(string interval) => interval switch
    {
        "1m"  => TimeSpan.FromMinutes(1),
        "3m"  => TimeSpan.FromMinutes(3),
        "5m"  => TimeSpan.FromMinutes(5),
        "15m" => TimeSpan.FromMinutes(15),
        "30m" => TimeSpan.FromMinutes(30),
        "1h"  => TimeSpan.FromHours(1),
        "2h"  => TimeSpan.FromHours(2),
        "4h"  => TimeSpan.FromHours(4),
        "6h"  => TimeSpan.FromHours(6),
        "8h"  => TimeSpan.FromHours(8),
        "12h" => TimeSpan.FromHours(12),
        "1d"  => TimeSpan.FromDays(1),
        _     => throw new ArgumentException($"Unsupported interval string: '{interval}'", nameof(interval))
    };
}
