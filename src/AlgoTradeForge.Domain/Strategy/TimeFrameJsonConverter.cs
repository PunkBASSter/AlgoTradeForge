using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlgoTradeForge.Domain.Strategy;

/// <summary>
/// Round-trips <see cref="TimeFrame"/> as its canonical shorthand <c>Code</c>
/// (<c>"1m"</c>, <c>"1h"</c>) on the wire. Read-side accepts either shorthand or
/// <c>TimeSpan.Parse</c> wire form (<c>"00:01:00"</c>) via
/// <see cref="TimeFrame.TryParseLiberal"/> — the boundary parser TRD §9.1 defines.
/// </summary>
/// <remarks>
/// Bypasses System.Text.Json's constructor-binding because <see cref="TimeFrame"/>
/// shadows its primary-ctor parameter with a get-only property to enforce
/// canonical-duration validation. Without this converter, STJ produces a
/// default-initialized struct (<c>Duration = TimeSpan.Zero</c>) on read.
/// </remarks>
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
