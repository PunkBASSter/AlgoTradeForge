using System.Globalization;
using AlgoTradeForge.Domain;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// P0-5 wire schema — converts the <c>POST /aggregate</c> request payload's threshold
/// (which can arrive in either <c>"absolute"</c> or <c>"convenience"</c> form) into:
/// <list type="bullet">
///   <item>the absolute decimal value persisted in <c>feeds.json</c>;</item>
///   <item>the long-typed scaled value the accumulator compares against;</item>
///   <item>the canonical feed-id component used to construct <see cref="AltBarFeedId.FeedId"/>.</item>
/// </list>
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

            // Phase 1b limits absolute thresholds to positive integers — sub-unit thresholds
            // (e.g. 0.5 base) must use convenience mode with the 'm' or 'u' SI suffix so the
            // feed-id grammar (positive integer mantissa) is satisfiable.
            if (absolute != Math.Truncate(absolute))
                throw new ArgumentException(
                    "Absolute thresholds must be integral in Phase 1b. Use input_mode=convenience with an SI suffix for sub-unit values.");

            feedIdComponent = ((long)absolute).ToString(CultureInfo.InvariantCulture);
            preservedConvenienceInput = null;
        }
        else
        {
            throw new ArgumentException($"Unrecognized input_mode '{inputMode}' (allowed: absolute, convenience).");
        }

        // Q-3 — pre-scale floor check. The smallest absolute value that scales to a non-zero
        // long depends on both the unit AND the asset's scale (see MinimumAbsolute). Throwing
        // BEFORE the scaling math gives an actionable message including the per-asset floor;
        // throwing AFTER would just say "underflowed to zero" with no hint at the boundary.
        var floor = MinimumAbsolute(thresholdUnit, scale);
        if (absolute < floor)
        {
            var hint = SuggestSiSuffix(floor);
            // Echo the original convenience-input string when present — preserves what the
            // user actually typed for log/UI replay; the resolved decimal alone (e.g. 0.000005)
            // doesn't tell the user that they wrote "5u".
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
            // in quote_asset (e.g. USD) projects via ScaleFactor × QuantityScale.
            "quote_asset" => scale.AmountToTicks(absolute * scale.QuantityScale),
            // Phase 5 (Range/Renko): threshold is a price magnitude (e.g. "$50 per bar"). Scale
            // mirrors how price is scaled — straight AmountToTicks, no * QuantityScale factor.
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
    /// Q-3 — the smallest absolute threshold value that scales to <c>1L</c> in the accumulator's
    /// long-typed domain for the given unit, on this asset's scale. Anything smaller would
    /// round to zero and degenerate the aggregator into a 1:1 bar-per-record copy of the source.
    /// </summary>
    /// <remarks>
    /// Computed entirely from <see cref="ScaleContext"/>'s public surface (<see cref="ScaleContext.TickSize"/>
    /// and <see cref="ScaleContext.QuantityScale"/>) — <c>ScaleFactor</c> stays internal per CLAUDE.md.
    /// Floor table:
    /// <list type="bullet">
    ///   <item><c>base_asset</c>: <c>1 / QuantityScale</c> (smallest <c>x</c> with <c>x * QuantityScale ≥ 1</c>).</item>
    ///   <item><c>quote_asset</c>: <c>TickSize / QuantityScale</c> (smallest <c>x</c> with <c>x * QuantityScale * ScaleFactor ≥ 1</c>;
    ///         since <c>ScaleFactor = 1 / TickSize</c>, the floor reduces to <c>TickSize / QuantityScale</c>).</item>
    ///   <item><c>trades</c>: <c>1</c> (integer count, scale-independent).</item>
    ///   <item><c>price</c>: <c>TickSize</c> (smallest <c>x</c> with <c>x * ScaleFactor ≥ 1</c>).</item>
    /// </list>
    /// </remarks>
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
    /// The threshold unit implied by an alt-bar type code (TRD §3.3 / §6.3). Re-aggregation
    /// eligibility already restricts the type-code chain (EqV→EqV, etc.), but the wire schema
    /// lets the user submit <c>threshold_unit</c> independently — endpoints check the submitted
    /// unit against this mapping so the comparison in the AltBar-source ordering branch
    /// (and the accumulator math itself) is apples-to-apples.
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
            "Range" => "price",
            "Renko" => "price",
            _ => throw new ArgumentException(
                $"Unrecognized type_code '{typeCode}' (allowed: EqV, EqT, EqD, EqI, Range, Renko)."),
        };
    }

    /// <summary>
    /// Picks the smallest SI suffix where <paramref name="value"/> renders as a positive integer
    /// mantissa, matching the <see cref="HistoryLoader.Domain.ThresholdValue"/> grammar. Returns
    /// <c>null</c> when no suffix produces an integer mantissa (caller falls back to the bare
    /// numeric form). Used only for the actionable error message — the wire grammar itself is
    /// unchanged.
    /// </summary>
    private static string? SuggestSiSuffix(decimal value)
    {
        if (value <= 0m) return null;

        // Try suffixes largest-first so the FIRST integer-mantissa match yields the SMALLEST
        // mantissa — which is the most natural form (e.g. "1m" rather than "1000u" for 0.001;
        // "1k" rather than "1000" for 1000).
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
