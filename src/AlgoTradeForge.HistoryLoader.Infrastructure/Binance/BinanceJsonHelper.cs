using System.Text.Json;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Binance;

internal static class BinanceJsonHelper
{
    /// <summary>
    /// Reads a required string from a named JSON property, throwing a descriptive
    /// <see cref="JsonException"/> instead of a <see cref="NullReferenceException"/>
    /// if the value is null.
    /// </summary>
    public static string ParseRequiredString(JsonElement element, string propertyName)
    {
        return element.GetProperty(propertyName).GetString()
            ?? throw new JsonException($"Required string property '{propertyName}' was null.");
    }

    /// <summary>
    /// Reads a required string from a JSON array element at the given index,
    /// throwing a descriptive <see cref="JsonException"/> if null.
    /// </summary>
    public static string ParseRequiredString(JsonElement element, int index)
    {
        return element.GetString()
            ?? throw new JsonException($"Required string value at index {index} was null.");
    }
}
