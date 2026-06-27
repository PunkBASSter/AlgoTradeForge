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
    int channelCapacity,
    Func<Asset> assetForAccount,
    Func<IReadOnlyList<string>> symbolsForAccount) : IAccountTargetFactory
{
    public async Task<IAccountTarget> Create(string account, CancellationToken ct = default)
    {
        var asset = assetForAccount();
        var seed = await funds.GetFreeFundsScaled(asset, ct);
        var portfolio = new Portfolio { InitialCash = seed };
        portfolio.Initialize();
        var ctx = new LiveOrderContext(portfolio, orderValidator, logger, orderClient, channelCapacity);
        ctx.Start(ct);
        return new AccountTarget(account, portfolio, ctx, orderClient, symbolsForAccount(), logger);
    }
}
