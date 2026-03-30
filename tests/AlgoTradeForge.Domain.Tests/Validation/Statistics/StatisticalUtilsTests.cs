using AlgoTradeForge.Domain.Validation.Statistics;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation.Statistics;

public class StatisticalUtilsTests
{
    [Fact]
    public void FisherYatesShuffle_PreservesAllElements()
    {
        var array = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var original = (double[])array.Clone();
        StatisticalUtils.FisherYatesShuffle(array, new Random(42));

        Array.Sort(array);
        Array.Sort(original);
        Assert.Equal(original, array);
    }

    [Fact]
    public void FisherYatesShuffle_Deterministic_SameSeed()
    {
        var a = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var b = (double[])a.Clone();

        StatisticalUtils.FisherYatesShuffle(a, new Random(123));
        StatisticalUtils.FisherYatesShuffle(b, new Random(123));

        Assert.Equal(a, b);
    }

    [Fact]
    public void FisherYatesShuffle_DifferentSeed_DifferentOrder()
    {
        var a = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var b = (double[])a.Clone();

        StatisticalUtils.FisherYatesShuffle(a, new Random(1));
        StatisticalUtils.FisherYatesShuffle(b, new Random(999));

        Assert.False(a.SequenceEqual(b));
    }

    [Fact]
    public void GetPercentile_EmptyArray_ReturnsZero()
    {
        Assert.Equal(0.0, StatisticalUtils.GetPercentile([], 50));
    }

    [Fact]
    public void GetPercentile_SingleElement_ReturnsThatElement()
    {
        Assert.Equal(42.0, StatisticalUtils.GetPercentile([42.0], 50));
    }

    [Fact]
    public void GetPercentile_KnownValues()
    {
        var sorted = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

        var p0 = StatisticalUtils.GetPercentile(sorted, 0);
        var p50 = StatisticalUtils.GetPercentile(sorted, 50);
        var p100 = StatisticalUtils.GetPercentile(sorted, 100);

        Assert.Equal(1.0, p0);
        Assert.True(p50 >= 5.0 && p50 <= 6.0);
        Assert.Equal(10.0, p100);
    }
}
