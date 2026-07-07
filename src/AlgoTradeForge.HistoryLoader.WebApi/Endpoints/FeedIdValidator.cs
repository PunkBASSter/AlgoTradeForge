using AlgoTradeForge.Domain.Aggregation;

namespace AlgoTradeForge.HistoryLoader.WebApi.Endpoints;

/// <summary>
/// Gates every API-boundary feed-id through the alt-bar grammar before it can flow into a
/// <c>DataFeedDescriptor</c> or path-resolution step. The grammar's character sets make
/// path-traversal injection (<c>..\..\evil</c>) impossible by construction.
/// </summary>
internal static class FeedIdValidator
{
    public static bool TryValidateAltBar(string feedId, out AltBarFeedId? parsed, out string? error)
    {
        if (AltBarFeedId.TryParse(feedId, out parsed, out error))
            return true;
        return false;
    }

    /// <summary>
    /// Validates a source feed-id. Side feeds have no positional grammar, so any non-traversal
    /// string is accepted; the catalog lookup that follows verifies existence.
    /// </summary>
    public static bool TryValidateSourceFeedId(string sourceFeedId, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(sourceFeedId))
        {
            error = "source_feed_id is required.";
            return false;
        }
        if (sourceFeedId.Contains("..") || sourceFeedId.Contains('/') || sourceFeedId.Contains('\\') || sourceFeedId.Contains(':'))
        {
            error = $"source_feed_id '{sourceFeedId}' contains illegal path characters.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Validates an exchange / asset path component. ASP.NET routing decodes <c>%2F</c> /
    /// <c>%5C</c> back to slashes that would otherwise reach the loader.
    /// </summary>
    public static bool TryValidatePathComponent(string value, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "path component is required.";
            return false;
        }
        // Whitelist beats blacklisting: also kills drive-relative roots ("C:evil") that the
        // old ..// \\ checks let through. ".." substring check catches "a..b"-style traversal.
        if (value is "." || value.Contains("..") || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            error = $"'{value}' contains characters outside [A-Za-z0-9._-].";
            return false;
        }
        return true;
    }
}
