using System.Text.Json;
using AlgoTradeForge.Application;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Strategies;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.WebApi.Contracts;

namespace AlgoTradeForge.WebApi.Tests.Contracts;

public sealed class TemplateRoundTripTests
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
    public void BacktestTemplate_RoundTrips()
    {
        var template = StrategyTemplateBuilder.BuildBacktestTemplate(
            "BuyAndHold", EmptyParams, NoAxes, SampleAssets);

        var json = JsonSerializer.Serialize(template, Json);
        var request = JsonSerializer.Deserialize<RunBacktestRequest>(json, Json);

        Assert.NotNull(request);
        Assert.Equal("BuyAndHold", request.StrategyName);
        Assert.NotNull(request.DataSubscription);
        Assert.Equal("BTCUSDT", request.DataSubscription.AssetName);
        Assert.Equal("Binance", request.DataSubscription.Exchange);
        Assert.NotNull(request.BacktestSettings);
        Assert.True(request.BacktestSettings.InitialCash > 0, "InitialCash should be positive");
    }

    [Fact]
    public void OptimizationTemplate_RoundTrips()
    {
        var template = StrategyTemplateBuilder.BuildOptimizationTemplate(
            "BuyAndHold", NoAxes, SampleAssets);

        var json = JsonSerializer.Serialize(template, Json);
        var request = JsonSerializer.Deserialize<RunOptimizationRequest>(json, Json);

        Assert.NotNull(request);
        Assert.Equal("BuyAndHold", request.StrategyName);
        Assert.NotNull(request.BacktestSettings);
        Assert.True(request.BacktestSettings.InitialCash > 0, "InitialCash should be positive");
        Assert.NotNull(request.SubscriptionAxis);
        Assert.NotEmpty(request.SubscriptionAxis);
        Assert.Equal("BTCUSDT", request.SubscriptionAxis[0].AssetName);
        Assert.Equal("Binance", request.SubscriptionAxis[0].Exchange);
    }

    [Fact]
    public void LiveSessionTemplate_RoundTrips()
    {
        var template = StrategyTemplateBuilder.BuildLiveSessionTemplate(
            "BuyAndHold", EmptyParams, NoAxes, SampleAssets);

        var json = JsonSerializer.Serialize(template, Json);
        var request = JsonSerializer.Deserialize<StartLiveSessionRequest>(json, Json);

        Assert.NotNull(request);
        Assert.Equal("BuyAndHold", request.StrategyName);
        Assert.True(request.InitialCash > 0, "InitialCash should be positive");
        Assert.NotNull(request.DataSubscriptions);
        Assert.NotEmpty(request.DataSubscriptions);
        Assert.Equal("BTCUSDT", request.DataSubscriptions[0].AssetName);
        Assert.Equal("Binance", request.DataSubscriptions[0].Exchange);
    }

    [Fact]
    public void DebugSessionTemplate_RoundTrips()
    {
        var template = StrategyTemplateBuilder.BuildDebugSessionTemplate(
            "BuyAndHold", EmptyParams, NoAxes, SampleAssets);

        var json = JsonSerializer.Serialize(template, Json);
        var request = JsonSerializer.Deserialize<StartDebugSessionRequest>(json, Json);

        Assert.NotNull(request);
        Assert.Equal("BuyAndHold", request.StrategyName);
        Assert.NotNull(request.DataSubscription);
        Assert.Equal("BTCUSDT", request.DataSubscription.AssetName);
        Assert.NotNull(request.BacktestSettings);
        Assert.True(request.BacktestSettings.InitialCash > 0, "InitialCash should be positive");
    }

    [Fact]
    public void BacktestTemplate_WithQuoteAssetParam_RoundTrips()
    {
        var axes = new ParameterAxis[]
        {
            new NumericRangeAxis("atrThreshold", Min: 1, Max: 100, Step: 1,
                ClrType: typeof(long), Unit: ParamUnit.QuoteAsset),
        };

        var paramDefaults = new Dictionary<string, object>
        {
            ["atrThreshold"] = 50_000L,
        };

        var template = StrategyTemplateBuilder.BuildBacktestTemplate(
            "BuyAndHold", paramDefaults, axes, SampleAssets);

        var json = JsonSerializer.Serialize(template, Json);
        var request = JsonSerializer.Deserialize<RunBacktestRequest>(json, Json);

        Assert.NotNull(request);
        Assert.Equal("BuyAndHold", request.StrategyName);
        Assert.NotNull(request.StrategyParameters);

        Assert.True(request.StrategyParameters.ContainsKey("atrThreshold"),
            "atrThreshold should be present in round-tripped parameters");

        var element = (JsonElement)request.StrategyParameters["atrThreshold"];
        Assert.Equal(JsonValueKind.Number, element.ValueKind);
        Assert.Equal(50m, element.GetDecimal());
    }
}
