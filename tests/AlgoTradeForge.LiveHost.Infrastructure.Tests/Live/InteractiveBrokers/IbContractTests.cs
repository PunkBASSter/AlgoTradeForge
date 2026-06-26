using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class IbContractTests
{
    [Fact]
    public void IbContract_ValueEquality_EnablesCacheKey()
    {
        var a = new IbContract("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD");
        var b = new IbContract("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void IbContract_DifferingField_NotEqual()
    {
        var a = new IbContract("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD");
        var b = a with { Currency = "EUR" };
        Assert.NotEqual(a, b);
    }
}
