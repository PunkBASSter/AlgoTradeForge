using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Infrastructure.Live;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public class CoTenancyRuleTests
{
    private static readonly CryptoAsset BtcUsdt = CryptoAsset.Create("BTCUSDT", "Binance",
        decimalDigits: 2, minOrderQuantity: 0.00001m, maxOrderQuantity: 9000m, quantityStepSize: 0.00001m);

    private static AccountTarget Seed(Asset seedAsset, string seedQuote)
    {
        var portfolio = new Portfolio { InitialCash = 50_000_00L };
        portfolio.Initialize();
        var client = Substitute.For<IExchangeOrderClient>();
        var ctx = new LiveOrderContext(portfolio, new OrderValidator(), NullLogger.Instance, client);
        ctx.Start(CancellationToken.None);
        return new AccountTarget("acctA", portfolio, ctx, client, seedAsset, seedQuote, NullLogger.Instance);
    }

    [Fact]
    public void Conflict_SameTickSameQuote_IsNull()
    {
        var target = Seed(BtcUsdt, "USDT");
        Assert.Null(CoTenancyRule.Conflict(target, BtcUsdt, "USDT"));
    }

    [Fact]
    public void Conflict_DifferentTick_ReportsScaleMismatch()
    {
        var target = Seed(BtcUsdt, "USDT"); // 2-dp tick = 0.01
        var dogeUsdt = CryptoAsset.Create("DOGEUSDT", "Binance",
            decimalDigits: 5, minOrderQuantity: 1m, maxOrderQuantity: 9_000_000m, quantityStepSize: 1m);

        var conflict = CoTenancyRule.Conflict(target, dogeUsdt, "USDT");

        Assert.NotNull(conflict);
        Assert.Contains("money scale", conflict);
    }

    [Fact]
    public void Conflict_SameTickDifferentQuote_ReportsCurrencyMismatch()
    {
        // BTCUSD vs BTCUSDT: identical 2-dp price tick, but different quote currency (USD vs USDT).
        // The scale fence passes; the currency fence must still reject it.
        var target = Seed(BtcUsdt, "USDT");
        var btcUsd = CryptoAsset.Create("BTCUSD", "Binance",
            decimalDigits: 2, minOrderQuantity: 0.00001m, maxOrderQuantity: 9000m, quantityStepSize: 0.00001m);

        var conflict = CoTenancyRule.Conflict(target, btcUsd, "USD");

        Assert.NotNull(conflict);
        Assert.Contains("quote currency", conflict);
    }

    [Fact]
    public void Conflict_QuoteComparison_IsCaseInsensitive()
    {
        var target = Seed(BtcUsdt, "USDT");
        Assert.Null(CoTenancyRule.Conflict(target, BtcUsdt, "usdt"));
    }
}
