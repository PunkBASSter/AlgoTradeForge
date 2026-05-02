using System.Text.Json;
using System.Text.Json.Serialization;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.WebApi.Contracts;

/// <summary>
/// Deserializes/serializes <c>subscriptionAxis</c> as a 2D array of subscription groups:
/// <c>[[{sub1}, {sub2}], [{sub3}, {sub4}]]</c>.
/// Every strategy uses the same format — single-subscription strategies simply have
/// one subscription per group: <c>[[{sub1}], [{sub2}]]</c>.
/// </summary>
/// <remarks>
/// The inner element shape is the polymorphic <see cref="DataFeedSubscription"/> hierarchy
/// (TRD §9.2): the wire carries a <c>"kind"</c> discriminator that STJ uses to dispatch to
/// the concrete record (<c>TimeBarSubscription</c> / <c>AltBarSubscription</c> /
/// <c>TickSubscription</c> / <c>SideFeedSubscription</c>). The 2D-array shape guard stays
/// in this converter — STJ's polymorphism handles per-element discrimination, but the outer
/// nesting is a contract assertion that fails fast with a clear message.
/// </remarks>
public sealed class SubscriptionAxisConverter : JsonConverter<List<List<DataFeedSubscription>>?>
{
    public override List<List<DataFeedSubscription>>? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("subscriptionAxis must be an array of arrays.");

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.GetArrayLength() == 0)
            return [];

        var result = new List<List<DataFeedSubscription>>(root.GetArrayLength());
        foreach (var groupElement in root.EnumerateArray())
        {
            if (groupElement.ValueKind != JsonValueKind.Array)
                throw new JsonException(
                    "subscriptionAxis must be a 2D array: [[{sub}, ...], ...]. " +
                    $"Expected inner array but found {groupElement.ValueKind}.");

            var group = new List<DataFeedSubscription>(groupElement.GetArrayLength());
            foreach (var subElement in groupElement.EnumerateArray())
            {
                var sub = subElement.Deserialize<DataFeedSubscription>(options)
                    ?? throw new JsonException("Failed to deserialize DataFeedSubscription in group.");
                group.Add(sub);
            }

            if (group.Count == 0)
                throw new JsonException("subscriptionAxis contains an empty group.");

            result.Add(group);
        }

        return result;
    }

    public override void Write(
        Utf8JsonWriter writer, List<List<DataFeedSubscription>>? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var group in value)
        {
            writer.WriteStartArray();
            foreach (var sub in group)
                JsonSerializer.Serialize(writer, sub, options);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }
}
