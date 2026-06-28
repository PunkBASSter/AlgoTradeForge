using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live;

public sealed class AccountTargetFactory(
    Func<string, Asset, IAccountFundsSource> fundsFor,
    Func<string, Asset, IExchangeOrderClient> clientFor,
    IOrderValidator orderValidator,
    ILogger logger,
    int channelCapacity) : IAccountTargetFactory
{
    public async Task<IAccountTarget> Create(string account, Asset executionAsset, CancellationToken ct = default)
    {
        var funds = fundsFor(account, executionAsset);
        var discovered = await funds.DiscoverFunds(executionAsset, ct);
        var portfolio = new Portfolio { InitialCash = discovered.FreeScaled };
        portfolio.Initialize();
        var orderClient = clientFor(account, executionAsset);
        var ctx = new LiveOrderContext(portfolio, orderValidator, logger, orderClient, channelCapacity);
        ctx.Start(ct);
        return new AccountTarget(account, portfolio, ctx, orderClient, executionAsset, discovered.QuoteAsset, logger);
    }
}
