using AlgoTradeForge.Domain.Optimization;
using AlgoTradeForge.Domain.Optimization.Space;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Optimization;

public class CartesianProductGeneratorTests
{
    private readonly CartesianProductGenerator _generator = new();

    [Fact]
    public void EmptyAxes_ReturnsSingleEmptyCombination()
    {
        var result = _generator.Enumerate([]).ToList();

        Assert.Single(result);
        Assert.Empty(result[0].Values);
    }

    [Fact]
    public void SingleNumericAxis_ReturnsCorrectValues()
    {
        var axes = new List<ResolvedAxis>
        {
            new ResolvedNumericAxis("Depth", [3m, 4m, 5m])
        };

        var result = _generator.Enumerate(axes).ToList();

        Assert.Equal(3, result.Count);
        Assert.Equal(3m, result[0].Values["Depth"]);
        Assert.Equal(4m, result[1].Values["Depth"]);
        Assert.Equal(5m, result[2].Values["Depth"]);
    }

    [Fact]
    public void TwoNumericAxes_ReturnsCartesianProduct()
    {
        var axes = new List<ResolvedAxis>
        {
            new ResolvedNumericAxis("Depth", [3m, 5m]),
            new ResolvedNumericAxis("Risk", [1m, 2m, 3m])
        };

        var result = _generator.Enumerate(axes).ToList();

        Assert.Equal(6, result.Count);

        // Verify all combinations exist
        var combos = result.Select(r => $"{r.Values["Depth"]}-{r.Values["Risk"]}").ToHashSet();
        Assert.Contains("3-1", combos);
        Assert.Contains("3-2", combos);
        Assert.Contains("3-3", combos);
        Assert.Contains("5-1", combos);
        Assert.Contains("5-2", combos);
        Assert.Contains("5-3", combos);
    }

    [Fact]
    public void EstimateCount_MatchesActualCount()
    {
        var axes = new List<ResolvedAxis>
        {
            new ResolvedNumericAxis("A", [1m, 2m, 3m]),
            new ResolvedNumericAxis("B", [10m, 20m]),
            new ResolvedDiscreteAxis("C", ["x", "y", "z"])
        };

        var estimate = _generator.EstimateCount(axes);
        var actual = _generator.Enumerate(axes).Count();

        Assert.Equal(18, estimate);
        Assert.Equal(estimate, actual);
    }

    [Fact]
    public void ModuleSlotAxis_WithTwoVariants_FlattenedCorrectly()
    {
        var axes = new List<ResolvedAxis>
        {
            new ResolvedModuleSlotAxis("ExitModule",
            [
                new ResolvedModuleVariant("AtrExit",
                [
                    new ResolvedNumericAxis("Multiplier", [2.0m, 3.0m])
                ]),
                new ResolvedModuleVariant("FibTp",
                [
                    new ResolvedNumericAxis("Level", [0.5m])
                ])
            ])
        };

        var result = _generator.Enumerate(axes).ToList();

        // 2 AtrExit + 1 FibTp = 3 combinations
        Assert.Equal(3, result.Count);

        var atrResults = result.Where(r =>
            r.Values["ExitModule"] is ModuleSelection ms && ms.TypeKey == "AtrExit").ToList();
        Assert.Equal(2, atrResults.Count);

        var fibResults = result.Where(r =>
            r.Values["ExitModule"] is ModuleSelection ms && ms.TypeKey == "FibTp").ToList();
        Assert.Single(fibResults);
    }

    [Fact]
    public void ModuleSlotAxis_WithSubAxes_EstimateMatchesActual()
    {
        var axes = new List<ResolvedAxis>
        {
            new ResolvedNumericAxis("Depth", [3m, 5m]),
            new ResolvedModuleSlotAxis("Exit",
            [
                new ResolvedModuleVariant("A",
                [
                    new ResolvedNumericAxis("P1", [1m, 2m]),
                    new ResolvedNumericAxis("P2", [10m, 20m, 30m])
                ]),
                new ResolvedModuleVariant("B", [])
            ])
        };

        // Depth: 2 values
        // Exit: A (2*3=6) + B (1) = 7
        // Total: 2 * 7 = 14
        var estimate = _generator.EstimateCount(axes);
        var actual = _generator.Enumerate(axes).Count();

        Assert.Equal(14, estimate);
        Assert.Equal(estimate, actual);
    }

    [Fact]
    public void EmptyAxes_EstimateCountReturnsOne()
    {
        Assert.Equal(1, _generator.EstimateCount([]));
    }

    [Fact]
    public void DiscreteAxis_ValuesPreserved()
    {
        var axes = new List<ResolvedAxis>
        {
            new ResolvedDiscreteAxis("Mode", ["Fast", "Slow"])
        };

        var result = _generator.Enumerate(axes).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal("Fast", result[0].Values["Mode"]);
        Assert.Equal("Slow", result[1].Values["Mode"]);
    }

    [Fact]
    public void LargeSpace_IsLazy_DoesNotOOM()
    {
        // 100 values per axis, 3 axes = 1,000,000 combinations
        var values = Enumerable.Range(1, 100).Select(i => (object)(decimal)i).ToList();
        var axes = new List<ResolvedAxis>
        {
            new ResolvedNumericAxis("A", values),
            new ResolvedNumericAxis("B", values),
            new ResolvedNumericAxis("C", values)
        };

        var estimate = _generator.EstimateCount(axes);
        Assert.Equal(1_000_000, estimate);

        // Take only first 10 — should not materialize entire space
        var first10 = _generator.Enumerate(axes).Take(10).ToList();
        Assert.Equal(10, first10.Count);
    }
}
