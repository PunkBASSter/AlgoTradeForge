using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlgoTradeForge.HistoryLoader.Application.Groups;

/// <summary>camelCase + null-fields omitted on write; used for group document files only.</summary>
public static class GroupJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy    = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition  = JsonIgnoreCondition.WhenWritingNull,
    };
}
