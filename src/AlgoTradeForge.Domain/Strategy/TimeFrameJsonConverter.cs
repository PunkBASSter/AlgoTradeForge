using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlgoTradeForge.Domain.Strategy;

/// <summary>
/// Round-trips <see cref="TimeFrame"/> as its canonical shorthand <c>Code</c>. Bypasses
/// STJ's ctor-binding because <see cref="TimeFrame"/> shadows its primary-ctor parameter
/// with a validating get-only property — without this converter, STJ produces a
/// default-initialized struct (<c>Duration = TimeSpan.Zero</c>) on read.
/// </summary>
public sealed class TimeFrameJsonConverter : JsonConverter<TimeFrame>
{
    public override TimeFrame Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        if (TimeFrame.TryParseLiberal(raw, out var tf)) return tf;
        throw new JsonException($"Invalid TimeFrame wire value: '{raw ?? "<null>"}'.");
    }

    public override void Write(Utf8JsonWriter writer, TimeFrame value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Code);
    }
}
