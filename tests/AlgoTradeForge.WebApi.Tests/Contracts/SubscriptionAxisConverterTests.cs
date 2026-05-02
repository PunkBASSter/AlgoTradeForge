using System.Text.Json;
using AlgoTradeForge.Application;
using AlgoTradeForge.WebApi.Contracts;
using Xunit;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.WebApi.Tests.Contracts;

public sealed class SubscriptionAxisConverterTests
{
    // Use the canonical policy (JsonDefaults.Api) and add the converter under test on top.
    // Copy-constructing from JsonDefaults.Api is the recommended pattern for consumers that
    // need a mutable instance — keeps the test wire shape in sync with the real wire shape.
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonDefaults.Api);
        options.Converters.Add(new SubscriptionAxisConverter());
        return options;
    }

    [Fact]
    public void RoundTrip_MultipleGroups_PreservesData()
    {
        List<List<DataFeedSubscription>> original =
        [
            [new TimeBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))],
            [new TimeBarSubscription("ETHUSDT", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))],
        ];

        var json = JsonSerializer.Serialize(original, Json);
        var deserialized = JsonSerializer.Deserialize<List<List<DataFeedSubscription>>>(json, Json);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Count);
        Assert.Equal("BTCUSDT", deserialized[0][0].AssetName);
        Assert.Equal("ETHUSDT", deserialized[1][0].AssetName);
    }

    [Fact]
    public void RoundTrip_MultiSubGroup_PreservesGroupStructure()
    {
        List<List<DataFeedSubscription>> original =
        [
            [
                new TimeBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h")),
                new TimeBarSubscription("BTCUSDT_PERP", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h")),
            ],
        ];

        var json = JsonSerializer.Serialize(original, Json);
        var deserialized = JsonSerializer.Deserialize<List<List<DataFeedSubscription>>>(json, Json);

        Assert.NotNull(deserialized);
        Assert.Single(deserialized);
        Assert.Equal(2, deserialized[0].Count);
        Assert.Equal("BTCUSDT", deserialized[0][0].AssetName);
        Assert.Equal("BTCUSDT_PERP", deserialized[0][1].AssetName);
    }

    [Fact]
    public void Deserialize_Null_ReturnsNull()
    {
        var result = JsonSerializer.Deserialize<List<List<DataFeedSubscription>>>("null", Json);

        Assert.Null(result);
    }

    [Fact]
    public void Serialize_Null_WritesNull()
    {
        var json = JsonSerializer.Serialize((List<List<DataFeedSubscription>>?)null, Json);

        Assert.Equal("null", json);
    }

    [Fact]
    public void Deserialize_EmptyOuterArray_ReturnsEmptyList()
    {
        var result = JsonSerializer.Deserialize<List<List<DataFeedSubscription>>>("[]", Json);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void Deserialize_FlatArray_ThrowsJsonException()
    {
        // A flat array of objects instead of a 2D array
        var json = """[{"assetName":"BTCUSDT","exchange":"Binance","timeFrame":"01:00:00"}]""";

        var ex = Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<List<List<DataFeedSubscription>>>(json, Json));
        Assert.Contains("2D array", ex.Message);
    }

    [Fact]
    public void Deserialize_EmptyInnerGroup_ThrowsJsonException()
    {
        var json = """[[], [{"assetName":"BTCUSDT","exchange":"Binance","timeFrame":"01:00:00"}]]""";

        var ex = Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<List<List<DataFeedSubscription>>>(json, Json));
        Assert.Contains("empty group", ex.Message);
    }

    [Fact]
    public void Deserialize_SingleSubGroup_Valid()
    {
        // Phase 4 P4-A: polymorphic shape requires `kind` discriminator (TRD §9.2 / [JsonPolymorphic]).
        // Role wire shape is the JsonStringEnumConverter form ("Primary"/"Side"); integer
        // ordinals are still accepted on read but the canonical wire shape is the string form.
        var json = """[[{"kind":"TimeBar","assetName":"BTCUSDT","exchange":"Binance","role":"Primary","timeFrame":"1h"}]]""";

        var result = JsonSerializer.Deserialize<List<List<DataFeedSubscription>>>(json, Json);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Single(result[0]);
        Assert.Equal("BTCUSDT", result[0][0].AssetName);
        Assert.IsType<TimeBarSubscription>(result[0][0]);
    }
}
