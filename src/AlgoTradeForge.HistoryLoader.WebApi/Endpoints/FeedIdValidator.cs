using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.WebApi.Endpoints;

/// <summary>
/// P1b-0b — gate every API-boundary feed-id through <see cref="AltBarFeedId.TryParse"/> /
/// <see cref="AltBarFeedId.AllowedSourceCodes"/> before it can flow into a
/// <c>DataFeedDescriptor</c> or path-resolution step. The grammar's character sets
/// (<c>EqT|EqV|EqD|EqI|Range|Renko</c>, <c>1m|5m|...|ticks</c>, digits + SI suffix) make
/// path-traversal injection (<c>..\..\evil</c>) impossible by construction — any string that
/// passes <see cref="AltBarFeedId.TryParse"/> is structurally safe.
/// </summary>
internal static class FeedIdValidator
{
    /// <summary>Validates an outcome alt-bar feed-id (e.g. "EqV_1m_1000", "EqI_ticks_500.flow").</summary>
    public static bool TryValidateAltBar(string feedId, out AltBarFeedId? parsed, out string? error)
    {
        if (AltBarFeedId.TryParse(feedId, out parsed, out error))
            return true;
        return false;
    }

    /// <summary>
    /// Validates a source feed-id — accepts the alt-bar grammar's source-code allowlist
    /// (<c>1m</c> ... <c>1d</c>, <c>ticks</c>) plus configured side-feed names. Side feeds
    /// don't have a positional grammar so we accept any non-traversal string for them.
    /// </summary>
    public static bool TryValidateSourceFeedId(string sourceFeedId, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(sourceFeedId))
        {
            error = "source_feed_id is required.";
            return false;
        }
        // Reject any path-separator or parent-dir tokens regardless of the configured-feed
        // allowlist. The catalog lookup that follows will additionally verify the feed exists.
        if (sourceFeedId.Contains("..") || sourceFeedId.Contains('/') || sourceFeedId.Contains('\\'))
        {
            error = $"source_feed_id '{sourceFeedId}' contains illegal path characters.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Validates an exchange / asset path component. ASP.NET routing decodes <c>%2F</c> /
    /// <c>%5C</c> back to slashes which would otherwise reach the loader; reject explicitly.
    /// </summary>
    public static bool TryValidatePathComponent(string value, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "path component is required.";
            return false;
        }
        if (value.Contains("..") || value.Contains('/') || value.Contains('\\'))
        {
            error = $"'{value}' contains illegal path characters.";
            return false;
        }
        return true;
    }
}
