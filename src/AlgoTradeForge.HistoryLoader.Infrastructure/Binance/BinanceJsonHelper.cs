using System.Globalization;
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
    /// Attempts to read a named JSON property as a <see langword="double"/>.
    /// Returns <see langword="false"/> when the value is null, empty, or not a valid number
    /// — allowing callers to skip malformed records instead of crashing.
    /// </summary>
    public static bool TryParseDouble(JsonElement element, string propertyName, out double result)
    {
        var str = element.GetProperty(propertyName).GetString();
        if (string.IsNullOrEmpty(str))
        {
            result = 0;
            return false;
        }

        return double.TryParse(str, NumberStyles.Float | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out result);
    }

    /// <summary>
    /// Attempts to parse a JSON string element as a <see langword="double"/>.
    /// Returns <see langword="false"/> when the value is null, empty, or not a valid number.
    /// </summary>
    public static bool TryParseDouble(JsonElement element, out double result)
    {
        var str = element.GetString();
        if (string.IsNullOrEmpty(str))
        {
            result = 0;
            return false;
        }

        return double.TryParse(str, NumberStyles.Float | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out result);
    }

    /// <summary>
    /// Attempts to parse a JSON string element as a <see langword="decimal"/>.
    /// Returns <see langword="false"/> when the value is null, empty, or not a valid number.
    /// </summary>
    public static bool TryParseDecimal(JsonElement element, out decimal result)
    {
        var str = element.GetString();
        if (string.IsNullOrEmpty(str))
        {
            result = 0;
            return false;
        }

        return decimal.TryParse(str, NumberStyles.Float | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out result);
    }
}
