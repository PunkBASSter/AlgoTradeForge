using AlgoTradeForge.HistoryLoader.Application;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

public sealed class HistoryLoaderOptionsValidatorTests
{
    private readonly HistoryLoaderOptionsValidator _validator = new();

    [Fact]
    public void Validate_DefaultOptions_Succeeds()
    {
        var options = new HistoryLoaderOptions();
        var result = _validator.Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ZeroConcurrency_Fails()
    {
        var options = new HistoryLoaderOptions { MaxBackfillConcurrency = 0 };
        var result = _validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains("MaxBackfillConcurrency", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Validate_BudgetOutOfRange_Fails(int budget)
    {
        var options = new HistoryLoaderOptions
        {
            Binance = new BinanceOptions { WeightBudgetPercent = budget }
        };
        var result = _validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains("WeightBudgetPercent", result.FailureMessage);
    }

    [Fact]
    public void Validate_GapMultiplierAtOne_Fails()
    {
        var options = new HistoryLoaderOptions
        {
            Assets =
            [
                new AssetCollectionConfig
                {
                    Symbol = "BTCUSDT",
                    Type = "perpetual",
                    Feeds = [new FeedCollectionConfig { Name = "candles", GapThresholdMultiplier = 1.0 }]
                }
            ]
        };
        var result = _validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains("GapThresholdMultiplier", result.FailureMessage);
    }

    [Fact]
    public void Validate_FeedHistoryStartInFuture_Fails()
    {
        var options = new HistoryLoaderOptions
        {
            Assets =
            [
                new AssetCollectionConfig
                {
                    Symbol = "BTCUSDT",
                    Type = "perpetual",
                    Feeds =
                    [
                        new FeedCollectionConfig
                        {
                            Name = "open-interest",
                            HistoryStart = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30)
                        }
                    ]
                }
            ]
        };
        var result = _validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains("HistoryStart", result.FailureMessage);
    }
}
