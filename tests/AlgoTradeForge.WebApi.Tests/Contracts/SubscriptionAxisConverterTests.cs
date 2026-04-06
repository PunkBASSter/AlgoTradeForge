using System.Text.Json;
using AlgoTradeForge.Application;
using AlgoTradeForge.WebApi.Contracts;
using Xunit;

namespace AlgoTradeForge.WebApi.Tests.Contracts;

public sealed class SubscriptionAxisConverterTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new SubscriptionAxisConverter() },
    };

    [Fact]
    public void RoundTrip_MultipleGroups_PreservesData()
    {
        List<List<DataSubscriptionDto>> original =
        [
            [new() { AssetName = "BTCUSDT", Exchange = "Binance", TimeFrame = "01:00:00" }],
            [new() { AssetName = "ETHUSDT", Exchange = "Binance", TimeFrame = "01:00:00" }],
        ];

        var json = JsonSerializer.Serialize(original, Json);
        var deserialized = JsonSerializer.Deserialize<List<List<DataSubscriptionDto>>>(json, Json);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Count);
        Assert.Equal("BTCUSDT", deserialized[0][0].AssetName);
        Assert.Equal("ETHUSDT", deserialized[1][0].AssetName);
    }

    [Fact]
    public void RoundTrip_MultiSubGroup_PreservesGroupStructure()
    {
        List<List<DataSubscriptionDto>> original =
        [
            [
                new() { AssetName = "BTCUSDT", Exchange = "Binance", TimeFrame = "01:00:00" },
                new() { AssetName = "BTCUSDT_PERP", Exchange = "Binance", TimeFrame = "01:00:00" },
            ],
        ];

        var json = JsonSerializer.Serialize(original, Json);
        var deserialized = JsonSerializer.Deserialize<List<List<DataSubscriptionDto>>>(json, Json);

        Assert.NotNull(deserialized);
        Assert.Single(deserialized);
        Assert.Equal(2, deserialized[0].Count);
        Assert.Equal("BTCUSDT", deserialized[0][0].AssetName);
        Assert.Equal("BTCUSDT_PERP", deserialized[0][1].AssetName);
    }

    [Fact]
    public void Deserialize_Null_ReturnsNull()
    {
        var result = JsonSerializer.Deserialize<List<List<DataSubscriptionDto>>>("null", Json);

        Assert.Null(result);
    }

    [Fact]
    public void Serialize_Null_WritesNull()
    {
        var json = JsonSerializer.Serialize((List<List<DataSubscriptionDto>>?)null, Json);

        Assert.Equal("null", json);
    }

    [Fact]
    public void Deserialize_EmptyOuterArray_ReturnsEmptyList()
    {
        var result = JsonSerializer.Deserialize<List<List<DataSubscriptionDto>>>("[]", Json);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void Deserialize_FlatArray_ThrowsJsonException()
    {
        // A flat array of objects instead of a 2D array
        var json = """[{"assetName":"BTCUSDT","exchange":"Binance","timeFrame":"01:00:00"}]""";

        var ex = Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<List<List<DataSubscriptionDto>>>(json, Json));
        Assert.Contains("2D array", ex.Message);
    }

    [Fact]
    public void Deserialize_EmptyInnerGroup_ThrowsJsonException()
    {
        var json = """[[], [{"assetName":"BTCUSDT","exchange":"Binance","timeFrame":"01:00:00"}]]""";

        var ex = Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<List<List<DataSubscriptionDto>>>(json, Json));
        Assert.Contains("empty group", ex.Message);
    }

    [Fact]
    public void Deserialize_SingleSubGroup_Valid()
    {
        var json = """[[{"assetName":"BTCUSDT","exchange":"Binance","timeFrame":"01:00:00"}]]""";

        var result = JsonSerializer.Deserialize<List<List<DataSubscriptionDto>>>(json, Json);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Single(result[0]);
        Assert.Equal("BTCUSDT", result[0][0].AssetName);
    }
}
