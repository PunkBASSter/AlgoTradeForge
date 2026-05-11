using System.Globalization;

namespace AlgoTradeForge.HistoryLoader.Domain;

/// <summary>
/// Positional parser/serializer for the alt-bar feed-id grammar:
/// <c>&lt;TypeCode&gt;_&lt;SourceCode&gt;_&lt;Threshold&gt;</c>, optionally suffixed with
/// <c>.flow</c> for the analytical sidecar of an aggregated bar feed.
/// </summary>
public sealed record AltBarFeedId(
    string TypeCode,
    string SourceCode,
    ThresholdValue Threshold,
    bool IsSidecar)
{
    public static readonly IReadOnlySet<string> AllowedTypeCodes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "EqT", "EqV", "EqD", "EqIV", "EqID", "EqIT", "Range", "Renko",
        };

    /// <summary>Allowed source codes: every interval string plus <c>"ticks"</c>.</summary>
    public static readonly IReadOnlySet<string> AllowedSourceCodes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "1m", "3m", "5m", "15m", "30m", "1h", "2h", "4h", "6h", "8h", "12h", "1d", "ticks",
        };

    /// <summary>The canonical feed-id (without the <c>.flow</c> sidecar suffix).</summary>
    public string FeedId =>
        $"{TypeCode}_{SourceCode}_{Threshold.ToCanonicalString()}";

    /// <summary>Directory name under <c>aggregated/</c> — <see cref="FeedId"/> plus <c>.flow</c> for sidecars.</summary>
    public string DirectoryName =>
        IsSidecar ? FeedId + ".flow" : FeedId;

    public static AltBarFeedId Parse(string text)
    {
        if (!TryParse(text, out var result, out var error))
            throw new FormatException($"Invalid alt-bar feed-id '{text}': {error}");
        return result!;
    }

    /// <summary>
    /// Strictly positional parse. <c>EqV_1m_500m</c> is unambiguous: component 2 is the
    /// source-code (matched against <see cref="AllowedSourceCodes"/>) and component 3 is the
    /// threshold mantissa+suffix. Do not disambiguate by scanning right-to-left or by content —
    /// underscores are the only separators that matter.
    /// </summary>
    public static bool TryParse(string text, out AltBarFeedId? result, out string? error)
    {
        result = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "empty input";
            return false;
        }

        var raw = text;
        var isSidecar = false;
        if (raw.EndsWith(".flow", StringComparison.Ordinal))
        {
            isSidecar = true;
            raw = raw[..^".flow".Length];
        }

        var parts = raw.Split('_');
        if (parts.Length != 3)
        {
            error = $"expected exactly two underscores producing 3 components; got {parts.Length}";
            return false;
        }

        var (typeCode, sourceCode, thresholdRaw) = (parts[0], parts[1], parts[2]);

        if (!AllowedTypeCodes.Contains(typeCode))
        {
            error = $"type-code '{typeCode}' not in allowed set";
            return false;
        }
        if (!AllowedSourceCodes.Contains(sourceCode))
        {
            error = $"source-code '{sourceCode}' not in allowed set";
            return false;
        }
        if (!ThresholdValue.TryParse(thresholdRaw, out var threshold, out var thresholdError))
        {
            error = $"threshold '{thresholdRaw}': {thresholdError}";
            return false;
        }

        result = new AltBarFeedId(typeCode, sourceCode, threshold, isSidecar);
        return true;
    }

    public override string ToString() => DirectoryName;
}

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
