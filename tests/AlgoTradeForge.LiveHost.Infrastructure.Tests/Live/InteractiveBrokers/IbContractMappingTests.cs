using AlgoTradeForge.Domain;
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class IbContractMappingTests
{
    [Fact]
    public void ToIbContract_Equity_RoutesSmartWithPrimaryExch()
    {
        var aapl = new EquityAsset { Name = "AAPL", Exchange = "NASDAQ" };

        var c = aapl.ToIbContract();

        Assert.Equal("AAPL", c.Symbol);
        Assert.Equal(IbSecType.Stk, c.SecType);
        Assert.Equal("SMART", c.Exchange);     // routing default
        Assert.Equal("NASDAQ", c.PrimaryExch); // listing <- Asset.Exchange
        Assert.Equal("USD", c.Currency);       // default; multi-currency deferred
    }

    [Fact]
    public void ToIbContract_Future_RoutesDirectExchangeNoPrimary()
    {
        var gold = FutureAsset.Create("GC", "COMEX", multiplier: 100m, tickSize: 0.1m);

        var c = gold.ToIbContract();

        Assert.Equal("GC", c.Symbol);
        Assert.Equal(IbSecType.Fut, c.SecType);
        Assert.Equal("COMEX", c.Exchange);  // futures route to the direct exchange <- Asset.Exchange
        Assert.Equal("", c.PrimaryExch);    // futures have no primary-listing exchange
        Assert.Equal("USD", c.Currency);
    }

    [Fact]
    public void ToIbContract_Crypto_NotSupported() =>
        Assert.Throws<NotSupportedException>(() =>
            CryptoAsset.Create("BTCUSDT", "Binance", decimalDigits: 2).ToIbContract());

    [Fact]
    public void ToIbContract_CryptoPerpetual_NotSupported() =>
        Assert.Throws<NotSupportedException>(() =>
            CryptoPerpetualAsset.Create("BTCUSDT", "Binance", decimalDigits: 2).ToIbContract());

    [Fact]
    public void ToAsset_Stk_BuildsEquityAsset()
    {
        var resolved = new ResolvedIbContract(
            new IbContract("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD"),
            ConId: 265598, LocalSymbol: "AAPL", LastTradeDate: "");

        var asset = resolved.ToAsset();

        var equity = Assert.IsType<EquityAsset>(asset);
        Assert.Equal("AAPL", equity.Name);
        Assert.Equal("NASDAQ", equity.Exchange);
    }

    [Fact]
    public void ToAsset_Fut_NotSupported_PendingEnrichment()
    {
        var resolved = new ResolvedIbContract(
            new IbContract("GC", IbSecType.Fut, "COMEX", "", "USD"),
            ConId: 1, LocalSymbol: "GCZ6", LastTradeDate: "20261229");
        Assert.Throws<NotSupportedException>(() => resolved.ToAsset());
    }
}
