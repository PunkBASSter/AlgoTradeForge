using System.Reflection;
using AlgoTradeForge.Domain.Reporting;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Reporting;

public class MetricNamesTests
{
    [Fact]
    public void EveryPerformanceMetricsProperty_HasCorrespondingConstant()
    {
        var metricProperties = typeof(PerformanceMetrics)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        var constants = typeof(MetricNames)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.Name != nameof(MetricNames.Default))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

        // Every PerformanceMetrics property must have a matching constant
        foreach (var prop in metricProperties)
        {
            Assert.True(constants.ContainsKey(prop),
                $"MetricNames is missing a constant for PerformanceMetrics.{prop}");
        }

        // Every constant (except Default) must map to a real property
        foreach (var (name, value) in constants)
        {
            Assert.True(metricProperties.Contains(value),
                $"MetricNames.{name} = \"{value}\" does not match any PerformanceMetrics property");
        }

        // Same count (no extras, no missing)
        Assert.Equal(metricProperties.Count, constants.Count);
    }

    [Fact]
    public void Default_IsSharpeRatio()
    {
        Assert.Equal(nameof(PerformanceMetrics.SharpeRatio), MetricNames.Default);
    }
}
