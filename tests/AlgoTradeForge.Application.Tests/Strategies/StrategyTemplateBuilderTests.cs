using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Strategies;
using AlgoTradeForge.Domain.Optimization.Space;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Strategies;

public sealed class StrategyTemplateBuilderTests
{
    private static readonly IReadOnlyList<ParameterAxis> NoAxes = [];

    // -----------------------------------------------------------------------
    // groupSize = 1 (single-subscription strategies)
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildOptimizationTemplate_SingleSub_MultipleAssets_WrapsEachInOwnGroup()
    {
        var assets = new List<AvailableAssetInfo>
        {
            new("Binance", "BTCUSDT", IsFutures: false),
            new("Binance", "ETHUSDT", IsFutures: false),
        };

        var template = StrategyTemplateBuilder.BuildOptimizationTemplate("Test", NoAxes, assets, requiredSubscriptionCount: 1);

        var groups = AssertSubscriptionAxis(template);
        Assert.Equal(2, groups.Count);
        Assert.Single(groups[0]);
        Assert.Single(groups[1]);
        Assert.Equal("BTCUSDT", GetAssetName(groups[0][0]));
        Assert.Equal("ETHUSDT", GetAssetName(groups[1][0]));
    }

    [Fact]
    public void BuildOptimizationTemplate_SingleSub_NoAssets_DefaultsBtcusdt()
    {
        var template = StrategyTemplateBuilder.BuildOptimizationTemplate("Test", NoAxes, [], requiredSubscriptionCount: 1);

        var groups = AssertSubscriptionAxis(template);
        Assert.Single(groups);
        Assert.Single(groups[0]);
        Assert.Equal("BTCUSDT", GetAssetName(groups[0][0]));
    }

    // -----------------------------------------------------------------------
    // groupSize = 2 (multi-subscription strategies, e.g. pairs trading)
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildOptimizationTemplate_PairsSub_MatchingPerps_PairsByNamePattern()
    {
        var assets = new List<AvailableAssetInfo>
        {
            new("Binance", "BTCUSDT", IsFutures: false),
            new("Binance", "BTCUSDT", IsFutures: true),   // LookupName = BTCUSDT_PERP
            new("Binance", "ETHUSDT", IsFutures: false),
            new("Binance", "ETHUSDT", IsFutures: true),   // LookupName = ETHUSDT_PERP
        };

        var template = StrategyTemplateBuilder.BuildOptimizationTemplate("PairsTest", NoAxes, assets, requiredSubscriptionCount: 2);

        var groups = AssertSubscriptionAxis(template);
        Assert.Equal(2, groups.Count);

        // Each group should have 2 subs: base + perp
        Assert.Equal(2, groups[0].Count);
        Assert.Equal("BTCUSDT", GetAssetName(groups[0][0]));
        Assert.Equal("BTCUSDT_PERP", GetAssetName(groups[0][1]));

        Assert.Equal(2, groups[1].Count);
        Assert.Equal("ETHUSDT", GetAssetName(groups[1][0]));
        Assert.Equal("ETHUSDT_PERP", GetAssetName(groups[1][1]));
    }

    [Fact]
    public void BuildOptimizationTemplate_PairsSub_NoMatchingPerps_ChunksSequentially()
    {
        // 4 spot assets, no perps — should fall back to sequential chunking
        var assets = new List<AvailableAssetInfo>
        {
            new("Binance", "BTCUSDT", IsFutures: false),
            new("Binance", "ETHUSDT", IsFutures: false),
            new("Binance", "SOLUSDT", IsFutures: false),
            new("Binance", "ADAUSDT", IsFutures: false),
        };

        var template = StrategyTemplateBuilder.BuildOptimizationTemplate("PairsTest", NoAxes, assets, requiredSubscriptionCount: 2);

        var groups = AssertSubscriptionAxis(template);
        Assert.Equal(2, groups.Count); // 4 assets / 2 per group = 2 groups
        Assert.Equal(2, groups[0].Count);
        Assert.Equal(2, groups[1].Count);
        Assert.Equal("BTCUSDT", GetAssetName(groups[0][0]));
        Assert.Equal("ETHUSDT", GetAssetName(groups[0][1]));
        Assert.Equal("SOLUSDT", GetAssetName(groups[1][0]));
        Assert.Equal("ADAUSDT", GetAssetName(groups[1][1]));
    }

    [Fact]
    public void BuildOptimizationTemplate_PairsSub_NoAssets_DefaultsPair()
    {
        var template = StrategyTemplateBuilder.BuildOptimizationTemplate("PairsTest", NoAxes, [], requiredSubscriptionCount: 2);

        var groups = AssertSubscriptionAxis(template);
        Assert.Single(groups);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal("BTCUSDT", GetAssetName(groups[0][0]));
        Assert.Equal("ETHUSDT_PERP", GetAssetName(groups[0][1]));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static List<List<Dictionary<string, object>>> AssertSubscriptionAxis(Dictionary<string, object> template)
    {
        Assert.True(template.ContainsKey("subscriptionAxis"), "Template must contain 'subscriptionAxis' key");
        var axis = template["subscriptionAxis"];
        var groups = Assert.IsType<List<List<Dictionary<string, object>>>>(axis);
        Assert.NotEmpty(groups);
        return groups;
    }

    private static string GetAssetName(Dictionary<string, object> sub) =>
        Assert.IsType<string>(sub["assetName"]);
}
