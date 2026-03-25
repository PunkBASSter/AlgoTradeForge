using System.Text.Json;

namespace AlgoTradeForge.Application;

public static class JsonDefaults
{
    public static JsonSerializerOptions Api { get; } = new(JsonSerializerDefaults.Web);
}
