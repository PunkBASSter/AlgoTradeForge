using System.Text.Json;
using AlgoTradeForge.Application;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using Xunit;

namespace AlgoTradeForge.Application.Tests;

/// <summary>
/// Pins the FE-bound wire shape produced by <see cref="JsonDefaults.Api"/>. Whereas
/// <c>DataFeedSubscriptionPolymorphismTests</c> documents the Domain default (no
/// converter, role = int), this test pins what FE clients actually receive over the
/// WebApi (with <c>JsonStringEnumConverter</c> applied).
/// </summary>
public sealed class JsonDefaultsTests
{
    [Fact]
    public void DataFeedRole_OnApiWire_SerializesAsString()
    {
        // The WebApi's ConfigureHttpJsonOptions delegates to JsonDefaults.Apply, which
        // adds JsonStringEnumConverter. So FE consumers see "Primary"/"Side" (PascalCase
        // by default for the converter), not 0/1.
        var sub = new TimeBarSubscription("BTC", "ex", DataFeedRole.Side, TimeFrame.Parse("1m"));

        var json = JsonSerializer.Serialize<DataFeedSubscription>(sub, JsonDefaults.Api);
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("role", out var roleEl));
        Assert.Equal(JsonValueKind.String, roleEl.ValueKind);
        Assert.Equal("Side", roleEl.GetString());
    }

    [Fact]
    public void DataFeedRole_OnApiWire_DeserializesFromString()
    {
        // Round-trip: a JSON payload with role: "Primary" deserializes back to the enum.
        // FE will emit string; the BE must accept it.
        const string json = """
            {
                "kind": "TimeBar",
                "assetName": "BTC",
                "exchange": "ex",
                "role": "Primary",
                "timeFrame": "1m"
            }
            """;

        var sub = JsonSerializer.Deserialize<DataFeedSubscription>(json, JsonDefaults.Api);

        Assert.NotNull(sub);
        Assert.Equal(DataFeedRole.Primary, sub!.Role);
        Assert.IsType<TimeBarSubscription>(sub);
    }

    [Fact]
    public void DataFeedRole_OnApiWire_AlsoAcceptsIntegerOnRead()
    {
        // JsonStringEnumConverter is forgiving — it accepts ints on read even though it
        // emits strings on write. Pins this so a stale FE client (or a hand-crafted JSON
        // payload from a tool that defaults to ints) doesn't fail loudly.
        const string json = """
            {
                "kind": "TimeBar",
                "assetName": "BTC",
                "exchange": "ex",
                "role": 0,
                "timeFrame": "1m"
            }
            """;

        var sub = JsonSerializer.Deserialize<DataFeedSubscription>(json, JsonDefaults.Api);

        Assert.NotNull(sub);
        Assert.Equal(DataFeedRole.Primary, sub!.Role);
    }
}
