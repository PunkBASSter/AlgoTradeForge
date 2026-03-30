using AlgoTradeForge.Domain.Validation.Statistics;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation.Statistics;

public class ClusterAnalyzerTests
{
    [Fact]
    public void TwoDistinctClusters_DetectedCorrectly()
    {
        // Two clearly separated clusters of parameter sets
        var paramSets = new List<IReadOnlyDictionary<string, object>>();
        var fitness = new List<double>();

        // Cluster 1: param1 ≈ 10, param2 ≈ 10
        for (var i = 0; i < 10; i++)
        {
            paramSets.Add(new Dictionary<string, object>
            {
                ["param1"] = 10.0 + i * 0.1,
                ["param2"] = 10.0 + i * 0.1,
            });
            fitness.Add(5.0 + i * 0.01); // High fitness
        }

        // Cluster 2: param1 ≈ 100, param2 ≈ 100
        for (var i = 0; i < 10; i++)
        {
            paramSets.Add(new Dictionary<string, object>
            {
                ["param1"] = 100.0 + i * 0.1,
                ["param2"] = 100.0 + i * 0.1,
            });
            fitness.Add(4.0 + i * 0.01); // Lower fitness
        }

        var result = ClusterAnalyzer.Analyze(paramSets, fitness, topPercentile: 0.50);

        Assert.True(result.ClusterCount >= 2);
        Assert.True(result.SilhouetteScore > 0);
    }

    [Fact]
    public void SingleTightCluster_HighConcentration()
    {
        // All points are identical — should cluster as one
        var paramSets = new List<IReadOnlyDictionary<string, object>>();
        var fitness = new List<double>();

        for (var i = 0; i < 20; i++)
        {
            paramSets.Add(new Dictionary<string, object>
            {
                ["param1"] = 50.0,
                ["param2"] = 30.0,
            });
            fitness.Add(3.0 + i * 0.01);
        }

        var result = ClusterAnalyzer.Analyze(paramSets, fitness);

        // With all identical points, k=1 is the best (no silhouette improvement possible)
        Assert.Equal(1, result.ClusterCount);
        Assert.Equal(1.0, result.PrimaryClusterConcentration);
    }

    [Fact]
    public void FewPoints_HandledGracefully()
    {
        var paramSets = new List<IReadOnlyDictionary<string, object>>
        {
            new Dictionary<string, object> { ["p"] = 1.0 },
            new Dictionary<string, object> { ["p"] = 2.0 },
            new Dictionary<string, object> { ["p"] = 3.0 },
        };
        var fitness = new List<double> { 5.0, 4.0, 3.0 };

        var result = ClusterAnalyzer.Analyze(paramSets, fitness, topPercentile: 1.0);

        Assert.True(result.PrimaryClusterConcentration > 0);
        Assert.True(result.ClusterCount >= 1);
    }

    [Fact]
    public void NonNumericParams_Ignored()
    {
        var paramSets = new List<IReadOnlyDictionary<string, object>>();
        var fitness = new List<double>();

        for (var i = 0; i < 10; i++)
        {
            paramSets.Add(new Dictionary<string, object>
            {
                ["name"] = "strategy_" + i, // Non-numeric — should be ignored
                ["value"] = (double)(i * 10),
            });
            fitness.Add(i + 1.0);
        }

        var result = ClusterAnalyzer.Analyze(paramSets, fitness, topPercentile: 0.50);

        // Should not crash and should produce a result
        Assert.True(result.PrimaryClusterConcentration > 0);
    }

    [Fact]
    public void NoNumericParams_ReturnsDefaultResult()
    {
        var paramSets = new List<IReadOnlyDictionary<string, object>>
        {
            new Dictionary<string, object> { ["name"] = "a" },
            new Dictionary<string, object> { ["name"] = "b" },
            new Dictionary<string, object> { ["name"] = "c" },
        };
        var fitness = new List<double> { 3, 2, 1 };

        var result = ClusterAnalyzer.Analyze(paramSets, fitness);

        Assert.Equal(1.0, result.PrimaryClusterConcentration);
        Assert.Equal(1, result.ClusterCount);
    }
}
