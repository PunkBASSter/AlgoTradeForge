using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;

public sealed class BinanceAccountTargetFactory(
    IAccountFundsSource funds,
    IExchangeOrderClient orderClient,
    IOrderValidator orderValidator,
    ILogger logger,
    int channelCapacity) : IAccountTargetFactory
{
    public async Task<IAccountTarget> Create(string account, Asset executionAsset, CancellationToken ct = default)
    {
        var discovered = await funds.DiscoverFunds(executionAsset, ct);
        var portfolio = new Portfolio { InitialCash = discovered.FreeScaled };
        portfolio.Initialize();
        var ctx = new LiveOrderContext(portfolio, orderValidator, logger, orderClient, channelCapacity);
        ctx.Start(ct);
        return new AccountTarget(account, portfolio, ctx, orderClient, executionAsset, discovered.QuoteAsset, logger);
    }
}
