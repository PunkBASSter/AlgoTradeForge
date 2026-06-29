using AlgoTradeForge.Domain;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public sealed class IbAccountFundsSourceTests
{
    private static EquityAsset Aapl => new() { Name = "AAPL", Exchange = "NASDAQ" };

    [Fact]
    public async Task DiscoverFunds_ReturnsAvailableFunds_ScaledAndCurrencyCorrect()
    {
        var asset = Aapl;
        var client = new FakeIbAccountSummaryClient([
            new IbAccountSummaryRow("DU123456", "AvailableFunds", "10000", "USD"),
            new IbAccountSummaryRow("DU123456", "NetLiquidation", "50000", "USD"),
        ]);

        var funds = await new IbAccountFundsSource(client)
            .DiscoverFunds("DU123456", asset, TestContext.Current.CancellationToken);

        var expected = new ScaleContext(asset).FromMarketPrice(10000m);
        Assert.Equal(expected, funds.FreeScaled);
        Assert.Equal("USD", funds.QuoteAsset);
    }

    [Fact]
    public async Task DiscoverFunds_MultipleTagsAndCurrencies_PicksAvailableFundsRow()
    {
        // Must ignore unrelated tags (BuyingPower, ExcessLiquidity) and return AvailableFunds.
        var asset = Aapl;
        var client = new FakeIbAccountSummaryClient([
            new IbAccountSummaryRow("DU123456", "BuyingPower", "80000", "USD"),
            new IbAccountSummaryRow("DU123456", "AvailableFunds", "25000", "USD"),
            new IbAccountSummaryRow("DU123456", "ExcessLiquidity", "5000", "USD"),
        ]);

        var funds = await new IbAccountFundsSource(client)
            .DiscoverFunds("DU123456", asset, TestContext.Current.CancellationToken);

        var expected = new ScaleContext(asset).FromMarketPrice(25000m);
        Assert.Equal(expected, funds.FreeScaled);
        Assert.Equal("USD", funds.QuoteAsset);
    }

    [Fact]
    public async Task DiscoverFunds_NoAvailableFundsTag_ReturnsZeroAndEmptyCurrency()
    {
        var asset = Aapl;
        var client = new FakeIbAccountSummaryClient([
            new IbAccountSummaryRow("DU123456", "NetLiquidation", "50000", "USD"),
        ]);

        var funds = await new IbAccountFundsSource(client)
            .DiscoverFunds("DU123456", asset, TestContext.Current.CancellationToken);

        Assert.Equal(0L, funds.FreeScaled);
        Assert.Equal("", funds.QuoteAsset);
    }

    [Fact]
    public async Task DiscoverFunds_MultipleAccounts_EachResolvesItsOwnFunds()
    {
        // One IB login spans N sub-accounts; the "All" summary returns a row per account. Each target
        // must be seeded from its OWN account's AvailableFunds, not whichever row arrives first.
        var asset = Aapl;
        var client = new FakeIbAccountSummaryClient([
            new IbAccountSummaryRow("DU111", "AvailableFunds", "10000", "USD"),
            new IbAccountSummaryRow("DU222", "AvailableFunds", "500000", "USD"),
        ]);
        var source = new IbAccountFundsSource(client);
        var ct = TestContext.Current.CancellationToken;

        var first = await source.DiscoverFunds("DU222", asset, ct);
        var second = await source.DiscoverFunds("DU111", asset, ct);

        Assert.Equal(new ScaleContext(asset).FromMarketPrice(500000m), first.FreeScaled);
        Assert.Equal(new ScaleContext(asset).FromMarketPrice(10000m), second.FreeScaled);
    }
}
