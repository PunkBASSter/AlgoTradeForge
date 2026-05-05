using System.Globalization;
using System.Text.Json.Serialization;
using AlgoTradeForge.Domain.Engine;

namespace AlgoTradeForge.Domain.Strategy;

/// <summary>
/// Strongly-typed bar interval. The constructor REJECTS non-canonical durations (e.g. 90s)
/// so every <c>TimeFrame</c> round-trips through <see cref="Code"/> — no silent precision
/// loss inside <c>Code</c>'s integer cast.
/// </summary>
[JsonConverter(typeof(TimeFrameJsonConverter))]
public readonly record struct TimeFrame(TimeSpan Duration)
{
    public TimeSpan Duration { get; } = Validate(Duration);

    /// <summary>Canonical shorthand code (e.g. <c>1m</c>, <c>1h</c>, <c>1d</c>).</summary>
    public string Code => TimeFrameFormatter.Format(Duration);

    /// <summary>
    /// Parses a shorthand like <c>"1m"</c>, <c>"15m"</c>, <c>"1h"</c>, <c>"1d"</c>.
    /// Throws <see cref="FormatException"/> on invalid input.
    /// </summary>
    public static TimeFrame Parse(string code) =>
        TryParse(code, out var tf)
            ? tf
            : throw new FormatException($"Invalid TimeFrame: '{code ?? "<null>"}'");

    /// <summary>Non-throwing variant of <see cref="Parse"/> (shorthand only).</summary>
    public static bool TryParse(string? code, out TimeFrame result)
    {
        if (TimeFrameFormatter.TryParseShorthand(code, out var ts))
        {
            result = new TimeFrame(ts);
            return true;
        }
        result = default;
        return false;
    }

    /// <summary>
    /// Boundary parser that accepts both shorthand (<c>"1m"</c>) AND <c>TimeSpan.Parse</c>
    /// wire form (<c>"00:01:00"</c>), but only when the resulting duration is canonical.
    /// Non-canonical inputs (<c>"00:01:30"</c>, <c>"00:00:00"</c>) return false rather than
    /// silently truncating in <see cref="Code"/>.
    /// </summary>
    public static bool TryParseLiberal(string? code, out TimeFrame result)
    {
        if (TryParse(code, out result)) return true;
        if (TimeSpan.TryParse(code, CultureInfo.InvariantCulture, out var ts) && IsCanonical(ts))
        {
            result = new TimeFrame(ts);
            return true;
        }
        result = default;
        return false;
    }

    public override string ToString() => Code;

    /// <summary>
    /// Implicit conversion to <see cref="TimeSpan"/>. The reverse conversion is intentionally
    /// NOT provided — that would let raw <c>TimeSpan</c> values silently pass through APIs that
    /// require a canonical bar interval.
    /// </summary>
    public static implicit operator TimeSpan(TimeFrame tf) => tf.Duration;

    private static TimeSpan Validate(TimeSpan duration)
    {
        if (!IsCanonical(duration))
            throw new ArgumentException(
                $"TimeFrame must be a canonical bar interval whose Code round-trips " +
                $"through TimeFrameFormatter (e.g. 1m, 15m, 1h, 1d). Got: {duration}.",
                nameof(duration));
        return duration;
    }

    private static bool IsCanonical(TimeSpan d)
    {
        if (d <= TimeSpan.Zero) return false;
        return TimeFrameFormatter.TryParseShorthand(TimeFrameFormatter.Format(d), out var rt) && rt == d;
    }
}
