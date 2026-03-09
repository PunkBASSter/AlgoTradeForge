using Xunit;

namespace AlgoTradeForge.Domain.Tests;

public class MoneyConvertTests
{
    [Theory]
    [InlineData(100.0, 100L)]
    [InlineData(0.0, 0L)]
    [InlineData(146.67, 147L)]       // Rounds up — was truncated to 146 before
    [InlineData(146.5, 147L)]        // Half rounds away from zero
    [InlineData(146.4, 146L)]        // Below half rounds down
    [InlineData(-146.5, -147L)]      // Negative half rounds away from zero
    [InlineData(-146.4, -146L)]      // Negative below half rounds toward zero
    [InlineData(0.5, 1L)]            // Half → 1
    [InlineData(-0.5, -1L)]          // Negative half → -1
    [InlineData(999999999.99, 1000000000L)]
    public void ToLong_RoundsCorrectly(decimal input, long expected)
    {
        Assert.Equal(expected, MoneyConvert.ToLong(input));
    }

    [Fact]
    public void ToLong_ExactInteger_ReturnsUnchanged()
    {
        Assert.Equal(42L, MoneyConvert.ToLong(42m));
    }

    [Fact]
    public void ToLong_LargeValue_DoesNotOverflow()
    {
        // Max safe long territory
        var result = MoneyConvert.ToLong(9_000_000_000_000m);
        Assert.Equal(9_000_000_000_000L, result);
    }
}
