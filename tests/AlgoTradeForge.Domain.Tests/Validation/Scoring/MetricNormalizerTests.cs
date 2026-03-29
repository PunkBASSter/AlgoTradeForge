using AlgoTradeForge.Domain.Validation.Scoring;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation.Scoring;

public sealed class MetricNormalizerTests
{
    // --- Normalize (higher is better) ---

    [Fact]
    public void Normalize_BelowFloor_ReturnsZero()
    {
        var result = MetricNormalizer.Normalize(10, floor: 30, excellent: 300);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void Normalize_AtFloor_ReturnsZero()
    {
        var result = MetricNormalizer.Normalize(30, floor: 30, excellent: 300);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void Normalize_Midpoint_ReturnsFifty()
    {
        // Midpoint between 30 and 300 = 165
        var result = MetricNormalizer.Normalize(165, floor: 30, excellent: 300);
        Assert.Equal(50.0, result, precision: 1);
    }

    [Fact]
    public void Normalize_AtExcellent_ReturnsHundred()
    {
        var result = MetricNormalizer.Normalize(300, floor: 30, excellent: 300);
        Assert.Equal(100.0, result);
    }

    [Fact]
    public void Normalize_AboveExcellent_ClampedToHundred()
    {
        var result = MetricNormalizer.Normalize(500, floor: 30, excellent: 300);
        Assert.Equal(100.0, result);
    }

    [Fact]
    public void Normalize_NaN_ReturnsZero()
    {
        var result = MetricNormalizer.Normalize(double.NaN, floor: 0, excellent: 100);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void Normalize_PositiveInfinity_ReturnsHundred()
    {
        var result = MetricNormalizer.Normalize(double.PositiveInfinity, floor: 0, excellent: 100);
        Assert.Equal(100.0, result);
    }

    [Fact]
    public void Normalize_NegativeInfinity_ReturnsZero()
    {
        var result = MetricNormalizer.Normalize(double.NegativeInfinity, floor: 0, excellent: 100);
        Assert.Equal(0.0, result);
    }

    [Theory]
    [InlineData(0.5, 0.0, 1.0, 50.0)]
    [InlineData(0.0, 0.0, 1.0, 0.0)]
    [InlineData(1.0, 0.0, 1.0, 100.0)]
    [InlineData(0.75, 0.0, 1.0, 75.0)]
    [InlineData(2.0, 1.0, 3.0, 50.0)]
    public void Normalize_VariousValues_ReturnsExpected(
        double value, double floor, double excellent, double expected)
    {
        var result = MetricNormalizer.Normalize(value, floor, excellent);
        Assert.Equal(expected, result, precision: 1);
    }

    // --- NormalizeInverted (lower is better) ---

    [Fact]
    public void NormalizeInverted_AtExcellent_ReturnsHundred()
    {
        // ddMultiplier: excellent=1.0 (best), floor=3.0 (worst)
        var result = MetricNormalizer.NormalizeInverted(1.0, floor: 3.0, excellent: 1.0);
        Assert.Equal(100.0, result);
    }

    [Fact]
    public void NormalizeInverted_AtFloor_ReturnsZero()
    {
        var result = MetricNormalizer.NormalizeInverted(3.0, floor: 3.0, excellent: 1.0);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void NormalizeInverted_BeyondFloor_ReturnsZero()
    {
        var result = MetricNormalizer.NormalizeInverted(5.0, floor: 3.0, excellent: 1.0);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void NormalizeInverted_BetterThanExcellent_ClampedToHundred()
    {
        var result = MetricNormalizer.NormalizeInverted(0.5, floor: 3.0, excellent: 1.0);
        Assert.Equal(100.0, result);
    }

    [Fact]
    public void NormalizeInverted_Midpoint_ReturnsFifty()
    {
        // Midpoint between 3.0 and 1.0 = 2.0
        var result = MetricNormalizer.NormalizeInverted(2.0, floor: 3.0, excellent: 1.0);
        Assert.Equal(50.0, result, precision: 1);
    }

    [Fact]
    public void NormalizeInverted_PValue_LowIsBetter()
    {
        // p-value: excellent=0.001, floor=0.5
        var result = MetricNormalizer.NormalizeInverted(0.05, floor: 0.5, excellent: 0.001);
        Assert.InRange(result, 85.0, 95.0); // 0.05 is quite good
    }

    [Fact]
    public void NormalizeInverted_NaN_ReturnsZero()
    {
        var result = MetricNormalizer.NormalizeInverted(double.NaN, floor: 3.0, excellent: 1.0);
        Assert.Equal(0.0, result);
    }
}
