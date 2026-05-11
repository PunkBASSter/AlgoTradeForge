using AlgoTradeForge.Application;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Domain.Optimization;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Optimization.Genetic;
using AlgoTradeForge.Domain.Optimization.Space;
using NSubstitute;
using Xunit;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.Application.Tests.Optimization;

public class EvaluateOptimizationQueryHandlerTests
{
    private readonly IOptimizationSpaceProvider _spaceProvider = Substitute.For<IOptimizationSpaceProvider>();
    private readonly OptimizationAxisResolver _axisResolver = new();
    private readonly ICartesianProductGenerator _cartesianGenerator = new CartesianProductGenerator();
    private readonly EvaluateOptimizationQueryHandler _handler;

    public EvaluateOptimizationQueryHandlerTests()
    {
        _handler = new EvaluateOptimizationQueryHandler(
            _spaceProvider, _axisResolver, _cartesianGenerator);
    }

    private static OptimizationSpaceDescriptor MakeDescriptor(
        string name, params ParameterAxis[] axes) =>
        new(name, typeof(object), typeof(object), axes);

    [Fact]
    public async Task BruteForce_WithKnownAxes_ReturnsCorrectCombinationCount()
    {
        var descriptor = MakeDescriptor("TestStrategy",
            new NumericRangeAxis("Period", 5, 50, 1, typeof(int), ParamUnit.Raw),
            new NumericRangeAxis("Multiplier", 1, 10, 1, typeof(int), ParamUnit.Raw));
        _spaceProvider.GetDescriptor("TestStrategy").Returns(descriptor);

        var query = new EvaluateOptimizationQuery
        {
            StrategyName = "TestStrategy",
            Axes = new Dictionary<string, OptimizationAxisOverride>
            {
                ["Period"] = new RangeOverride(10, 14, 1),       // 5 values
                ["Multiplier"] = new RangeOverride(2, 4, 1),     // 3 values
            },
            Mode = "BruteForce",
        };

        var result = await _handler.HandleAsync(query, TestContext.Current.CancellationToken);

        Assert.Equal(15, result.TotalCombinations); // 5 * 3
        Assert.False(result.ExceedsMaxCombinations);
        Assert.Equal(500_000, result.MaxCombinations);
        Assert.Null(result.GeneticConfig);
    }

    [Fact]
    public async Task BruteForce_ExceedsMaxCombinations_FlagSetCorrectly()
    {
        var descriptor = MakeDescriptor("TestStrategy",
            new NumericRangeAxis("Period", 1, 1000, 1, typeof(int), ParamUnit.Raw));
        _spaceProvider.GetDescriptor("TestStrategy").Returns(descriptor);

        var query = new EvaluateOptimizationQuery
        {
            StrategyName = "TestStrategy",
            Axes = new Dictionary<string, OptimizationAxisOverride>
            {
                ["Period"] = new RangeOverride(1, 1000, 1), // 1000 values
            },
            MaxCombinations = 500,
            Mode = "BruteForce",
        };

        var result = await _handler.HandleAsync(query, TestContext.Current.CancellationToken);

        Assert.Equal(1000, result.TotalCombinations);
        Assert.True(result.ExceedsMaxCombinations);
        Assert.Equal(500, result.MaxCombinations);
    }

    [Fact]
    public async Task Genetic_ReturnsAutoSizedConfig()
    {
        var descriptor = MakeDescriptor("TestStrategy",
            new NumericRangeAxis("A", 1, 100, 1, typeof(int), ParamUnit.Raw),
            new NumericRangeAxis("B", 1, 100, 1, typeof(int), ParamUnit.Raw),
            new NumericRangeAxis("C", 1, 100, 1, typeof(int), ParamUnit.Raw),
            new NumericRangeAxis("D", 1, 100, 1, typeof(int), ParamUnit.Raw));
        _spaceProvider.GetDescriptor("TestStrategy").Returns(descriptor);

        var query = new EvaluateOptimizationQuery
        {
            StrategyName = "TestStrategy",
            Axes = new Dictionary<string, OptimizationAxisOverride>
            {
                ["A"] = new RangeOverride(1, 10, 1),
                ["B"] = new RangeOverride(1, 10, 1),
                ["C"] = new RangeOverride(1, 10, 1),
                ["D"] = new RangeOverride(1, 10, 1),
            },
            Mode = "Genetic",
        };

        var result = await _handler.HandleAsync(query, TestContext.Current.CancellationToken);

        Assert.NotNull(result.GeneticConfig);
        Assert.Equal(4, result.EffectiveDimensions);
        // Auto-sized: 10 * ceil(sqrt(4)) = 10 * 2 = 20, clamped to [50, 500] → 50
        Assert.Equal(50, result.GeneticConfig.PopulationSize);
        // MaxEvals: 50 * 200 = 10_000
        Assert.Equal(10_000, result.GeneticConfig.MaxEvaluations);
        // MaxGens: 10_000 / 50 = 200
        Assert.Equal(200, result.GeneticConfig.MaxGenerations);
        // MutationRate: 1.0 / 4 = 0.25
        Assert.Equal(0.25, result.GeneticConfig.MutationRate);
    }

    [Fact]
    public async Task Genetic_WithUserOverrides_PreservesOverrides()
    {
        var descriptor = MakeDescriptor("TestStrategy",
            new NumericRangeAxis("A", 1, 100, 1, typeof(int), ParamUnit.Raw));
        _spaceProvider.GetDescriptor("TestStrategy").Returns(descriptor);

        var query = new EvaluateOptimizationQuery
        {
            StrategyName = "TestStrategy",
            Axes = new Dictionary<string, OptimizationAxisOverride>
            {
                ["A"] = new RangeOverride(1, 10, 1),
            },
            Mode = "Genetic",
            GeneticSettings = new GeneticConfig
            {
                PopulationSize = 200,
                MaxEvaluations = 5000,
            },
        };

        var result = await _handler.HandleAsync(query, TestContext.Current.CancellationToken);

        Assert.NotNull(result.GeneticConfig);
        Assert.Equal(200, result.GeneticConfig.PopulationSize);
        Assert.Equal(5000, result.GeneticConfig.MaxEvaluations);
        // MaxGens auto-sized: 5000 / 200 = 25
        Assert.Equal(25, result.GeneticConfig.MaxGenerations);
    }

    [Fact]
    public async Task UnknownStrategy_ThrowsArgumentException()
    {
        _spaceProvider.GetDescriptor("NoSuch").Returns((IOptimizationSpaceDescriptor?)null);

        var query = new EvaluateOptimizationQuery
        {
            StrategyName = "NoSuch",
            Mode = "BruteForce",
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => _handler.HandleAsync(query, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SubscriptionAxis_CountedCorrectly()
    {
        var descriptor = MakeDescriptor("TestStrategy",
            new NumericRangeAxis("Period", 5, 50, 1, typeof(int), ParamUnit.Raw));
        _spaceProvider.GetDescriptor("TestStrategy").Returns(descriptor);

        var query = new EvaluateOptimizationQuery
        {
            StrategyName = "TestStrategy",
            Axes = new Dictionary<string, OptimizationAxisOverride>
            {
                ["Period"] = new RangeOverride(10, 12, 1), // 3 values
            },
            SubscriptionAxis =
            [
                [new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))],
                [new TimeBarSubscription("ETHUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))],
            ],
            Mode = "BruteForce",
        };

        var result = await _handler.HandleAsync(query, TestContext.Current.CancellationToken);

        // Per-DSS group mode: 3 param values per run (not multiplied by 2 DSS)
        Assert.Equal(3, result.TotalCombinations);
        Assert.Equal(2, result.DssCount);
    }

    [Fact]
    public async Task SingleDataSubscription_NoSubscriptionAxis()
    {
        var descriptor = MakeDescriptor("TestStrategy",
            new NumericRangeAxis("Period", 5, 50, 1, typeof(int), ParamUnit.Raw));
        _spaceProvider.GetDescriptor("TestStrategy").Returns(descriptor);

        var query = new EvaluateOptimizationQuery
        {
            StrategyName = "TestStrategy",
            Axes = new Dictionary<string, OptimizationAxisOverride>
            {
                ["Period"] = new RangeOverride(10, 12, 1), // 3 values
            },
            SubscriptionAxis =
            [
                [new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))],
            ],
            Mode = "BruteForce",
        };

        var result = await _handler.HandleAsync(query, TestContext.Current.CancellationToken);

        // 3 values * 1 (single subscription group, no axis variation) = 3
        Assert.Equal(3, result.TotalCombinations);
    }

    [Fact]
    public async Task MultipleDataSubscriptions_TreatedAsAxis()
    {
        var descriptor = MakeDescriptor("TestStrategy",
            new NumericRangeAxis("Period", 5, 50, 1, typeof(int), ParamUnit.Raw));
        _spaceProvider.GetDescriptor("TestStrategy").Returns(descriptor);

        var query = new EvaluateOptimizationQuery
        {
            StrategyName = "TestStrategy",
            Axes = new Dictionary<string, OptimizationAxisOverride>
            {
                ["Period"] = new RangeOverride(10, 12, 1), // 3 values
            },
            SubscriptionAxis =
            [
                [new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))],
                [new TimeBarSubscription("ETHUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))],
                [new TimeBarSubscription("SOLUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))],
            ],
            Mode = "BruteForce",
        };

        var result = await _handler.HandleAsync(query, TestContext.Current.CancellationToken);

        // Per-DSS group mode: 3 values per run (not multiplied by 3 DSS)
        Assert.Equal(3, result.TotalCombinations);
        Assert.Equal(3, result.DssCount);
    }

    [Fact]
    public async Task ModuleSlotAxes_CountedCorrectly()
    {
        var moduleAxis = new ModuleSlotAxis("Filter", typeof(object), [
            new ModuleVariantDescriptor("TypeA", typeof(object), typeof(object), [
                new NumericRangeAxis("SubParam1", 1, 10, 1, typeof(int), ParamUnit.Raw),
            ]),
            new ModuleVariantDescriptor("TypeB", typeof(object), typeof(object), [
                new NumericRangeAxis("SubParam1", 1, 10, 1, typeof(int), ParamUnit.Raw),
                new NumericRangeAxis("SubParam2", 1, 5, 1, typeof(int), ParamUnit.Raw),
            ]),
        ]);
        var descriptor = MakeDescriptor("TestStrategy", moduleAxis);
        _spaceProvider.GetDescriptor("TestStrategy").Returns(descriptor);

        var query = new EvaluateOptimizationQuery
        {
            StrategyName = "TestStrategy",
            Axes = new Dictionary<string, OptimizationAxisOverride>
            {
                ["Filter"] = new ModuleChoiceOverride(new Dictionary<string, Dictionary<string, OptimizationAxisOverride>?>
                {
                    ["TypeA"] = new Dictionary<string, OptimizationAxisOverride>
                    {
                        ["SubParam1"] = new RangeOverride(1, 3, 1), // 3 values
                    },
                    ["TypeB"] = new Dictionary<string, OptimizationAxisOverride>
                    {
                        ["SubParam1"] = new RangeOverride(1, 2, 1), // 2 values
                        ["SubParam2"] = new RangeOverride(1, 2, 1), // 2 values
                    },
                }),
            },
            Mode = "BruteForce",
        };

        var result = await _handler.HandleAsync(query, TestContext.Current.CancellationToken);

        // TypeA: 3 combinations, TypeB: 2*2=4 combinations → total = 7
        Assert.Equal(7, result.TotalCombinations);
    }

    [Fact]
    public async Task BruteForce_NoGeneticConfig()
    {
        var descriptor = MakeDescriptor("TestStrategy",
            new NumericRangeAxis("Period", 5, 50, 1, typeof(int), ParamUnit.Raw));
        _spaceProvider.GetDescriptor("TestStrategy").Returns(descriptor);

        var query = new EvaluateOptimizationQuery
        {
            StrategyName = "TestStrategy",
            Axes = new Dictionary<string, OptimizationAxisOverride>
            {
                ["Period"] = new RangeOverride(10, 12, 1),
            },
            Mode = "BruteForce",
        };

        var result = await _handler.HandleAsync(query, TestContext.Current.CancellationToken);

        Assert.Null(result.GeneticConfig);
        Assert.True(result.EffectiveDimensions >= 1);
    }

    [Fact]
    public async Task Genetic_NeverExceedsMaxCombinations()
    {
        var descriptor = MakeDescriptor("TestStrategy",
            new NumericRangeAxis("Period", 1, 1000, 1, typeof(int), ParamUnit.Raw));
        _spaceProvider.GetDescriptor("TestStrategy").Returns(descriptor);

        var query = new EvaluateOptimizationQuery
        {
            StrategyName = "TestStrategy",
            Axes = new Dictionary<string, OptimizationAxisOverride>
            {
                ["Period"] = new RangeOverride(1, 1000, 1), // 1000 values — exceeds MaxCombinations
            },
            MaxCombinations = 500,
            Mode = "Genetic",
        };

        var result = await _handler.HandleAsync(query, TestContext.Current.CancellationToken);

        Assert.Equal(1000, result.TotalCombinations);
        // Genetic mode ignores MaxCombinations — cost is governed by MaxEvaluations
        Assert.False(result.ExceedsMaxCombinations);
        Assert.NotNull(result.GeneticConfig);
    }

    [Fact]
    public async Task MultiPrimaryDss_DssCountReflectsExpansion()
    {
        // Phase 4 (P4-14, TRD §9.6): a single DSS with multiple Role=Primary entries
        // should preview as |primaries| separate child runs (post-expansion), so the
        // FE's "dssCount × combosPerRun" cost preview math matches what the submission
        // handler will actually enqueue.
        var descriptor = MakeDescriptor("TestStrategy",
            new NumericRangeAxis("Period", 5, 50, 1, typeof(int), ParamUnit.Raw));
        _spaceProvider.GetDescriptor("TestStrategy").Returns(descriptor);

        var query = new EvaluateOptimizationQuery
        {
            StrategyName = "TestStrategy",
            Axes = new Dictionary<string, OptimizationAxisOverride>
            {
                ["Period"] = new RangeOverride(10, 12, 1), // 3 values
            },
            // One DSS, three Role=Primary entries — should expand to 3 single-primary DSSes.
            SubscriptionAxis =
            [
                [
                    new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1h")),
                    new TimeBarSubscription("ETHUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1h")),
                    new TimeBarSubscription("SOLUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1h")),
                ],
            ],
            Mode = "BruteForce",
        };

        var result = await _handler.HandleAsync(query, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.TotalCombinations); // per-run, unchanged
        Assert.Equal(3, result.DssCount);          // post-expansion: 3 fan-out children
    }

    [Fact]
    public async Task MultiPrimaryWithSharedSide_ExpansionPreservesSideForEachChild()
    {
        // [Primary(BTC), Primary(ETH), Side(funding-rate)] → 2 expanded DSSes,
        // each carrying the funding-rate side feed. Cost preview: 2 children.
        var descriptor = MakeDescriptor("TestStrategy",
            new NumericRangeAxis("Period", 5, 50, 1, typeof(int), ParamUnit.Raw));
        _spaceProvider.GetDescriptor("TestStrategy").Returns(descriptor);

        var query = new EvaluateOptimizationQuery
        {
            StrategyName = "TestStrategy",
            Axes = new Dictionary<string, OptimizationAxisOverride>
            {
                ["Period"] = new RangeOverride(10, 12, 1),
            },
            SubscriptionAxis =
            [
                [
                    new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1h")),
                    new TimeBarSubscription("ETHUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1h")),
                    new SideFeedSubscription("BTCUSDT", "binance", DataFeedRole.Side, "funding-rate"),
                ],
            ],
            Mode = "BruteForce",
        };

        var result = await _handler.HandleAsync(query, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.DssCount); // 2 fan-out children, side feed shared
    }

    [Fact]
    public async Task EmptyAxes_ReturnsOneCombination()
    {
        var descriptor = MakeDescriptor("TestStrategy",
            new NumericRangeAxis("Period", 5, 50, 1, typeof(int), ParamUnit.Raw));
        _spaceProvider.GetDescriptor("TestStrategy").Returns(descriptor);

        var query = new EvaluateOptimizationQuery
        {
            StrategyName = "TestStrategy",
            Mode = "BruteForce",
        };

        var result = await _handler.HandleAsync(query, TestContext.Current.CancellationToken);

        // No axes overrides → all axes resolve to empty → filtered out → EstimateCount([]) = 1
        Assert.Equal(1, result.TotalCombinations);
    }
}
