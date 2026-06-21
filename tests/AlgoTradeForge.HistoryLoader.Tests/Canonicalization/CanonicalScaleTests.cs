using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Canonicalization;

public sealed class CanonicalScaleTests
{
    [Theory]
    [InlineData(5000050L, 2, 50000.5)]   // price exp 2
    [InlineData(123L, 3, 0.123)]         // qty exp 3
    [InlineData(42L, 0, 42.0)]           // exp 0 == identity
    [InlineData(5L, -1, 50.0)]           // negative exp multiplies
    public void Unscale_DividesByPowerOfTen(long raw, int exp, double expected)
    {
        Assert.Equal(expected, CanonicalScale.Unscale(raw, (sbyte)exp), precision: 10);
    }

    [Theory]
    [InlineData(AggressorSide.Sell, 1.0)]
    [InlineData(AggressorSide.Buy, 0.0)]
    [InlineData(AggressorSide.Unknown, 0.0)]
    public void ToIsBuyerMaker_SellIsOne(AggressorSide side, double expected)
    {
        Assert.Equal(expected, CanonicalScale.ToIsBuyerMaker(side));
    }
}
