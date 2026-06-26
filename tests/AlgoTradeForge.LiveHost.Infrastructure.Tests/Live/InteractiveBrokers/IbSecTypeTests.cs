using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class IbSecTypeTests
{
    [Theory]
    [InlineData(IbSecType.Stk, "STK")]
    [InlineData(IbSecType.Fut, "FUT")]
    internal void ToIbString_MapsEachMember(IbSecType type, string expected) =>
        Assert.Equal(expected, type.ToIbString());

    [Theory]
    [InlineData("STK", IbSecType.Stk)]
    [InlineData("FUT", IbSecType.Fut)]
    internal void FromIbString_RoundTrips(string raw, IbSecType expected) =>
        Assert.Equal(expected, IbSecTypeExtensions.FromIbString(raw));

    [Fact]
    public void FromIbString_Unknown_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => IbSecTypeExtensions.FromIbString("OPT"));
}
