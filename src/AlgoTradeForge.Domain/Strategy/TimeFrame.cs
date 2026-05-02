using System.Globalization;
using System.Text.Json.Serialization;
using AlgoTradeForge.Domain.Engine;

namespace AlgoTradeForge.Domain.Strategy;

/// <summary>
/// Strongly-typed bar interval. Wraps <see cref="TimeSpan"/> so the API can distinguish a
/// timeframe ("1m", "1h") from an arbitrary duration. Phase 4 of the alternative-bars
/// redesign (TRD §9.1) replaces raw <c>TimeSpan</c> in subscription / loader / strategy
/// surfaces. Read-side arithmetic and comparisons go through <see cref="Duration"/>.
/// </summary>
/// <remarks>
/// Format/parse is delegated to <see cref="TimeFrameFormatter"/> — the canonical lowercase
/// shorthand grammar (<c>1m</c>, <c>15m</c>, <c>1h</c>, <c>1d</c>) shared with feed-id
/// composition (TRD §3.3). The constructor REJECTS non-canonical durations (e.g. 90s) so
/// every <c>TimeFrame</c> round-trips through <see cref="Code"/>; this guards the
/// type-safety contract Phase 4 set out to enforce — no silent precision loss inside
/// <c>Code</c>'s integer cast.
/// </remarks>
/// <param name="Duration">Underlying bar interval. Must round-trip through <see cref="Code"/>.</param>
[JsonConverter(typeof(TimeFrameJsonConverter))]
public readonly record struct TimeFrame(TimeSpan Duration)
{
    public TimeSpan Duration { get; } = Validate(Duration);

    /// <summary>Canonical shorthand code (e.g. <c>1m</c>, <c>1h</c>, <c>1d</c>).</summary>
    public string Code => TimeFrameFormatter.Format(Duration);

    /// <summary>
    /// Parses a shorthand like <c>"1m"</c>, <c>"15m"</c>, <c>"1h"</c>, <c>"1d"</c>.
    /// Throws <see cref="FormatException"/> on invalid input — mirroring
    /// <c>TimeSpan.Parse</c>'s contract for symmetry. Use <see cref="TryParse"/> for the
    /// non-throwing form.
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
    /// wire form (<c>"00:01:00"</c>), but only when the resulting duration is canonical
    /// (round-trips through <see cref="Code"/>). Used at command/request boundaries where
    /// payloads from different consumers carry either form (TRD §9.1). Non-canonical
    /// inputs (<c>"00:01:30"</c>, <c>"00:00:00"</c>) return false rather than silently
    /// truncating in <see cref="Code"/>.
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
    /// Implicit conversion to <see cref="TimeSpan"/> so existing read-side code (arithmetic,
    /// comparisons, <c>Resample(...)</c>, <c>.TotalMilliseconds</c>) keeps working without
    /// per-callsite <c>.Duration</c> sprinkles. The reverse conversion is intentionally NOT
    /// provided — that would let raw <c>TimeSpan</c> values silently pass through APIs that
    /// require a meaningful bar interval, which is exactly what TRD §9.1 set out to prevent.
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
