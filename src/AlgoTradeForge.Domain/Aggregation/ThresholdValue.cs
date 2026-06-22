using System.Globalization;

namespace AlgoTradeForge.Domain.Aggregation;

/// <summary>
/// Threshold expressed as integer mantissa + optional SI suffix. Sub-unit thresholds
/// (<c>m</c> = 10⁻³, <c>u</c> = 10⁻⁶) are representable without a decimal point in the feed-id.
/// </summary>
public readonly record struct ThresholdValue(long Mantissa, char Suffix)
{
    public const char NoSuffix = '\0';

    /// <summary>Absolute value in canonical units.</summary>
    public decimal AbsoluteValue => Mantissa * SuffixMultiplier(Suffix);

    public static decimal SuffixMultiplier(char suffix) => suffix switch
    {
        NoSuffix => 1m,
        'k' => 1_000m,
        'M' => 1_000_000m,
        'G' => 1_000_000_000m,
        'm' => 0.001m,
        'u' => 0.000001m,
        _ => throw new ArgumentException($"Unrecognized SI suffix '{suffix}'", nameof(suffix)),
    };

    public string ToCanonicalString() =>
        Suffix == NoSuffix
            ? Mantissa.ToString(CultureInfo.InvariantCulture)
            : Mantissa.ToString(CultureInfo.InvariantCulture) + Suffix;

    public static bool TryParse(string text, out ThresholdValue result, out string? error)
    {
        result = default;
        error = null;

        if (string.IsNullOrEmpty(text))
        {
            error = "empty";
            return false;
        }

        var lastChar = text[^1];
        var hasSuffix = lastChar is 'k' or 'M' or 'G' or 'm' or 'u';
        var digits = hasSuffix ? text[..^1] : text;

        if (digits.Length == 0)
        {
            error = "no digits before suffix";
            return false;
        }

        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var mantissa))
        {
            error = $"mantissa '{digits}' is not a positive integer";
            return false;
        }

        if (mantissa <= 0)
        {
            error = "mantissa must be positive";
            return false;
        }

        if (char.IsLetter(lastChar) && !hasSuffix)
        {
            error = $"unrecognized suffix '{lastChar}' (allowed: k, M, G, m, u)";
            return false;
        }

        result = new ThresholdValue(mantissa, hasSuffix ? lastChar : NoSuffix);
        return true;
    }
}
