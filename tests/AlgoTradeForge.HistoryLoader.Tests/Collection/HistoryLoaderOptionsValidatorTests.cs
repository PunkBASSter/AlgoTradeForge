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

    // -------------------------------------------------------------------------
    // P1b-19 — Aggregator knobs validation (TRD §6.5)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("MaxConcurrentJobs")]
    [InlineData("MaxConcurrentTickJobs")]
    [InlineData("MaxQueueDepth")]
    [InlineData("JobRetentionMinutes")]
    [InlineData("MaxPartitionSizeMB")]
    public void Validate_AggregatorKnobNonPositive_Fails(string field)
    {
        var aggregator = field switch
        {
            "MaxConcurrentJobs"     => new AggregatorOptions { MaxConcurrentJobs = 0 },
            "MaxConcurrentTickJobs" => new AggregatorOptions { MaxConcurrentTickJobs = 0 },
            "MaxQueueDepth"         => new AggregatorOptions { MaxQueueDepth = 0 },
            "JobRetentionMinutes"   => new AggregatorOptions { JobRetentionMinutes = 0 },
            "MaxPartitionSizeMB"    => new AggregatorOptions { MaxPartitionSizeMB = 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        var options = new HistoryLoaderOptions { Aggregator = aggregator };
        var result = _validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(field, result.FailureMessage);
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
    public void Validate_ValidSchedule_Succeeds()
    {
        var options = new HistoryLoaderOptions
        {
            Schedules = new() { ["daily"] = new() { Cron = "30 16 * * 1-5", TimeZone = "UTC" } }
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_InvalidCron_Fails()
    {
        var options = new HistoryLoaderOptions
        {
            Schedules = new() { ["bad"] = new() { Cron = "not a cron", TimeZone = "UTC" } }
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("cron", result.FailureMessage);
    }

    [Fact]
    public void Validate_InvalidTimeZone_Fails()
    {
        var options = new HistoryLoaderOptions
        {
            Schedules = new() { ["tz"] = new() { Cron = "0 0 * * *", TimeZone = "Mars/Olympus" } }
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("TimeZone", result.FailureMessage);
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
