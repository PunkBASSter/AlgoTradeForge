using System.Text.Json;
using AlgoTradeForge.Application;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Strategies;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.LiveHost.WebApi.Contracts;

namespace AlgoTradeForge.LiveHost.WebApi.Tests.Contracts;

public sealed class LiveSessionTemplateRoundTripTests
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Api;

    private static readonly IReadOnlyList<AvailableAssetInfo> SampleAssets =
    [
        new AvailableAssetInfo("Binance", "BTCUSDT", IsFutures: false),
    ];

    private static readonly IReadOnlyDictionary<string, object> EmptyParams =
        new Dictionary<string, object>();

    private static readonly IReadOnlyList<ParameterAxis> NoAxes = [];

    [Fact]
    public void LiveSessionTemplate_RoundTrips()
    {
        var template = StrategyTemplateBuilder.BuildLiveSessionTemplate(
            "BuyAndHold", EmptyParams, NoAxes, SampleAssets);

        var json = JsonSerializer.Serialize(template, Json);
        var request = JsonSerializer.Deserialize<StartLiveSessionRequest>(json, Json);

        Assert.NotNull(request);
        Assert.Equal("BuyAndHold", request.StrategyName);
        Assert.NotNull(request.DataSubscriptions);
        Assert.NotEmpty(request.DataSubscriptions);
        Assert.Equal("BTCUSDT", request.DataSubscriptions[0].AssetName);
        Assert.Equal("Binance", request.DataSubscriptions[0].Exchange);
    }
}
