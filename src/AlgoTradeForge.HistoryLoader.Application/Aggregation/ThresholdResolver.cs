using System.Globalization;
using AlgoTradeForge.Domain;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Converts the <c>POST /aggregate</c> threshold (absolute or convenience form) into the
/// absolute decimal persisted in <c>feeds.json</c>, the long-typed scaled value the
/// accumulator compares against, and the canonical feed-id component.
/// </summary>
public static class ThresholdResolver
{
    public sealed record Resolved(
        decimal Absolute,
        long Scaled,
        string FeedIdComponent,
        string? PreservedConvenienceInput);

    public static Resolved Resolve(
        string thresholdUnit,
        string inputMode,
        decimal? thresholdValue,
        string? convenienceInput,
        ScaleContext scale)
    {
        ArgumentException.ThrowIfNullOrEmpty(thresholdUnit);
        ArgumentException.ThrowIfNullOrEmpty(inputMode);

        decimal absolute;
        string feedIdComponent;
        string? preservedConvenienceInput;

        if (string.Equals(inputMode, "convenience", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(convenienceInput))
                throw new ArgumentException("convenience_input is required when input_mode=convenience.");
            if (!ThresholdValue.TryParse(convenienceInput, out var tv, out var err))
                throw new ArgumentException($"convenience_input '{convenienceInput}': {err}");
            absolute = tv.AbsoluteValue;
            feedIdComponent = convenienceInput;
            preservedConvenienceInput = convenienceInput;
        }
        else if (string.Equals(inputMode, "absolute", StringComparison.OrdinalIgnoreCase))
        {
            if (thresholdValue is null || thresholdValue.Value <= 0m)
                throw new ArgumentException("threshold must be a positive value when input_mode=absolute.");
            absolute = thresholdValue.Value;

            // Absolute thresholds are limited to positive integers; sub-unit thresholds
            // (e.g. 0.5 base) must use convenience mode with an SI suffix so the feed-id grammar
            // (positive integer mantissa) is satisfiable.
            if (absolute != Math.Truncate(absolute))
                throw new ArgumentException(
                    "Absolute thresholds must be integral. Use input_mode=convenience with an SI suffix for sub-unit values.");

            feedIdComponent = ((long)absolute).ToString(CultureInfo.InvariantCulture);
            preservedConvenienceInput = null;
        }
        else
        {
            throw new ArgumentException($"Unrecognized input_mode '{inputMode}' (allowed: absolute, convenience).");
        }

        // Pre-scale floor check. Throwing BEFORE the scaling math gives an actionable message
        // including the per-asset floor; throwing AFTER would just say "underflowed to zero".
        var floor = MinimumAbsolute(thresholdUnit, scale);
        if (absolute < floor)
        {
            var hint = SuggestSiSuffix(floor);
            // Echo the original convenience-input when present so the user sees what they typed
            // (e.g. "5u" rather than the resolved 0.000005).
            var inputDisplay = preservedConvenienceInput is not null
                ? $"{absolute} ({preservedConvenienceInput}) {thresholdUnit}"
                : $"{absolute} {thresholdUnit}";
            throw new ArgumentException(
                $"Threshold {inputDisplay} is below this asset's minimum of {floor} {thresholdUnit}" +
                (hint is null ? "." : $" (use convenience input '{hint}' or larger)."));
        }

        long scaled = thresholdUnit.ToLowerInvariant() switch
        {
            "base_asset" => MoneyConvert.ToLong(absolute * scale.QuantityScale),
            "trades"     => (long)absolute,
            // EqD per-record contribution is (Close × Volume) in tick × quant units; threshold
            // in quote_asset projects via ScaleFactor × QuantityScale.
            "quote_asset" => scale.AmountToTicks(absolute * scale.QuantityScale),
            // Range/Renko: threshold is a price magnitude — straight AmountToTicks, no
            // QuantityScale factor.
            "price" => scale.AmountToTicks(absolute),
            _ => throw new ArgumentException(
                $"Unrecognized threshold_unit '{thresholdUnit}' (allowed: base_asset, quote_asset, trades, price)."),
        };

        // Defense-in-depth: if MinimumAbsolute is ever wrong (or a future unit forgets a case),
        // surface the underflow rather than enqueue a 1:1-bar-per-record runaway job.
        if (scaled <= 0)
            throw new ArgumentException(
                $"Resolved threshold underflowed to zero (absolute={absolute}, unit={thresholdUnit}, floor={floor}). " +
                "This indicates a MinimumAbsolute bug — please report.");

        return new Resolved(absolute, scaled, feedIdComponent, preservedConvenienceInput);
    }

    /// <summary>
    /// Smallest absolute threshold value that scales to <c>1L</c> for the given unit on this
    /// asset's scale. Anything smaller rounds to zero and degenerates the aggregator into a
    /// 1:1 bar-per-record copy of the source.
    /// </summary>
    public static decimal MinimumAbsolute(string thresholdUnit, ScaleContext scale)
    {
        ArgumentException.ThrowIfNullOrEmpty(thresholdUnit);
        return thresholdUnit.ToLowerInvariant() switch
        {
            "base_asset" => 1m / scale.QuantityScale,
            "quote_asset" => scale.TickSize / scale.QuantityScale,
            "trades" => 1m,
            "price" => scale.TickSize,
            _ => throw new ArgumentException(
                $"Unrecognized threshold_unit '{thresholdUnit}' (allowed: base_asset, quote_asset, trades, price)."),
        };
    }

    /// <summary>
    /// Threshold unit implied by an alt-bar type code. The wire schema lets the user submit
    /// <c>threshold_unit</c> independently; endpoints check it against this mapping so
    /// comparisons stay apples-to-apples.
    /// </summary>
    public static string GetImplicitUnit(string typeCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeCode);
        return typeCode switch
        {
            "EqV" => "base_asset",
            "EqT" => "trades",
            "EqD" => "quote_asset",
            "EqI" => "base_asset",
            "EqID" => "quote_asset",
            "EqIT" => "trades",
            "Range" => "price",
            "Renko" => "price",
            _ => throw new ArgumentException(
                $"Unrecognized type_code '{typeCode}' (allowed: EqV, EqT, EqD, EqI, EqID, EqIT, Range, Renko)."),
        };
    }

    // Picks the smallest SI suffix where value renders as a positive integer mantissa,
    // matching the ThresholdValue grammar. Returns null when no suffix produces an integer
    // mantissa. Used only for the actionable error message.
    private static string? SuggestSiSuffix(decimal value)
    {
        if (value <= 0m) return null;

        // Try suffixes largest-first so the first integer-mantissa match yields the smallest
        // mantissa — the most natural form (e.g. "1m" rather than "1000u" for 0.001).
        (string Suffix, decimal Multiplier)[] candidates =
        [
            ("G", 1_000_000_000m),
            ("M", 1_000_000m),
            ("k", 1_000m),
            ("",  1m),
            ("m", 0.001m),
            ("u", 0.000001m),
        ];

        foreach (var (suffix, mult) in candidates)
        {
            var mantissa = value / mult;
            if (mantissa >= 1m && mantissa == Math.Truncate(mantissa) && mantissa <= long.MaxValue)
                return ((long)mantissa).ToString(System.Globalization.CultureInfo.InvariantCulture) + suffix;
        }
        return null;
    }
}
