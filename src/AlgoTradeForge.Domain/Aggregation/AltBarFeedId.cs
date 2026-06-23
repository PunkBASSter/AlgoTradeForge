namespace AlgoTradeForge.Domain.Aggregation;

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
