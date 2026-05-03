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

        if (scaled <= 0)
            throw new ArgumentException(
                $"Resolved threshold underflowed to zero (absolute={absolute}, unit={thresholdUnit}). " +
                "Q-3 in the task tracker tracks the canonical floor.");

        return new Resolved(absolute, scaled, feedIdComponent, preservedConvenienceInput);
    }
}
