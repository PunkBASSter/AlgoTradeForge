using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Infrastructure.Live;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public class BinanceAccountTargetFactoryTests
{
    private static readonly Asset TestAsset =
        CryptoAsset.Create("BTCUSDT", "Binance", decimalDigits: 2);

    private static BinanceAccountTargetFactory BuildFactory(IAccountFundsSource fundsSource) =>
        new(
            fundsSource,
            Substitute.For<IExchangeOrderClient>(),
            new OrderValidator(),
            NullLogger.Instance,
            channelCapacity: 64);

    [Fact]
    public async Task Create_SeedsPortfolioInitialCash_FromDiscoveredFunds()
    {
        var ct = TestContext.Current.CancellationToken;
        const long expectedSeed = 12_345_00L;

        var fundsSource = Substitute.For<IAccountFundsSource>();
        fundsSource.DiscoverFunds(TestAsset, Arg.Any<CancellationToken>())
            .Returns(new AccountFunds(expectedSeed, "USDT"));

        var factory = BuildFactory(fundsSource);
        await using var target = await factory.Create("acctA", TestAsset, ct);

        Assert.Equal(expectedSeed, target.Portfolio.InitialCash);
        // The quote currency the funds source discovered is stamped on the target (immutable seed).
        Assert.Equal("USDT", ((AccountTarget)target).SeedQuoteAsset);
    }
}
