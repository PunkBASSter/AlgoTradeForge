using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class IbContractTranslationTests
{
    [Fact]
    public void ToIbApiContract_Equity_MapsEveryField()
    {
        var spec = new IbContract("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD");

        var ib = spec.ToIbApiContract();

        Assert.Equal("AAPL", ib.Symbol);
        Assert.Equal("STK", ib.SecType);
        Assert.Equal("SMART", ib.Exchange);
        Assert.Equal("NASDAQ", ib.PrimaryExch);
        Assert.Equal("USD", ib.Currency);
        Assert.Equal(0, ib.ConId); // unresolved until reqContractDetails
    }

    [Fact]
    public void ToIbApiContract_Future_SendsNoExpiry()
    {
        var spec = new IbContract("GC", IbSecType.Fut, "COMEX", "", "USD");

        var ib = spec.ToIbApiContract();

        Assert.Equal("GC", ib.Symbol);
        Assert.Equal("FUT", ib.SecType);
        Assert.Equal("COMEX", ib.Exchange);
        // expiry-less so IB returns all listed months for front-month selection
        Assert.True(string.IsNullOrEmpty(ib.LastTradeDateOrContractMonth));
    }

    [Fact]
    public void ToIbApiContract_ResolvedContract_SetsConIdAndPreservesSpecFields()
    {
        var spec = new IbContract("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD");
        var resolved = new ResolvedIbContract(spec, ConId: 265598, LocalSymbol: "AAPL", LastTradeDate: "");

        var ib = resolved.ToIbApiContract();

        Assert.Equal(265598, ib.ConId);
        Assert.Equal("AAPL", ib.Symbol);
        Assert.Equal("STK", ib.SecType);
        Assert.Equal("SMART", ib.Exchange);
        Assert.Equal("USD", ib.Currency);
    }
}
