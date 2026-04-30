using System.Text.Json.Nodes;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Storage;

/// <summary>
/// JSON-aware validation of <c>feeds.json</c> content. Rules expressed here are ones
/// that cannot be enforced by <see cref="System.Text.Json.JsonSerializer"/> alone — most
/// notably "the property MUST be present, even if null" (TRD §4).
/// </summary>
internal static class FeedMetadataValidator
{
    /// <summary>
    /// Validates a parsed <c>feeds.json</c> document. Throws <see cref="FeedMetadataValidationException"/>
    /// on the first violation; the message names the offending feed-id and rule.
    /// </summary>
    public static void ValidateOrThrow(JsonNode? root)
    {
        if (root is not JsonObject obj) return;
        if (obj["feeds"] is not JsonObject feeds) return;

        foreach (var (feedId, feedNode) in feeds)
        {
            if (feedNode is not JsonObject feed) continue;

            var kind = feed["kind"]?.GetValue<string?>();
            if (kind != "aggregated") continue;

            // TRD §4: every aggregated feed entry MUST have a fidelity block.
            if (feed["fidelity"] is not JsonObject fidelity)
                throw new FeedMetadataValidationException(
                    $"Feed '{feedId}' (kind=aggregated) is missing the required 'fidelity' block.");

            // TRD §4: imbalanceReconstructionMethod MUST be present even on non-EqI feeds (null OK).
            // Absence (vs explicit null) signals a malformed manifest.
            if (!fidelity.ContainsKey("imbalanceReconstructionMethod"))
                throw new FeedMetadataValidationException(
                    $"Feed '{feedId}': fidelity.imbalanceReconstructionMethod must be present " +
                    "(use null for non-EqI feeds; field absence indicates a malformed manifest).");
        }
    }
}

internal sealed class FeedMetadataValidationException : Exception
{
    public FeedMetadataValidationException(string message) : base(message) { }
}
