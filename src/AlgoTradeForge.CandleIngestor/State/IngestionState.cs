using System.Text.Json.Serialization;

namespace AlgoTradeForge.CandleIngestor.State;

public sealed class IngestionState
{
    [JsonPropertyName("firstTimestamp")]
    public DateTimeOffset? FirstTimestamp { get; set; }

    [JsonPropertyName("lastTimestamp")]
    public DateTimeOffset? LastTimestamp { get; set; }

    [JsonPropertyName("lastRunUtc")]
    public DateTimeOffset? LastRunUtc { get; set; }

    [JsonPropertyName("gaps")]
    public List<IngestionGap> Gaps { get; set; } = [];
}

public sealed class IngestionGap
{
    [JsonPropertyName("from")]
    public DateTimeOffset From { get; set; }

    [JsonPropertyName("to")]
    public DateTimeOffset To { get; set; }
}
