using System.Text.Json.Nodes;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Storage;

/// <summary>
/// JSON-aware validation of <c>feeds.json</c> for rules JsonSerializer can't express —
/// notably "property MUST be present, even if null".
/// </summary>
internal static class FeedMetadataValidator
{
    public static void ValidateOrThrow(JsonNode? root)
    {
        if (root is not JsonObject obj) return;
        if (obj["feeds"] is not JsonObject feeds) return;

        foreach (var (feedId, feedNode) in feeds)
        {
            if (feedNode is not JsonObject feed) continue;

            var kind = feed["kind"]?.GetValue<string?>();
            if (kind != "aggregated") continue;

            if (feed["fidelity"] is not JsonObject fidelity)
                throw new FeedMetadataValidationException(
                    $"Feed '{feedId}' (kind=aggregated) is missing the required 'fidelity' block.");

            // imbalanceReconstructionMethod must be present even on non-EqI feeds (explicit null OK);
            // absence vs null distinguishes a malformed manifest from a valid non-EqI entry.
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
