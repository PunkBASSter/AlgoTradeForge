using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Live;

namespace AlgoTradeForge.Application.Live;

public sealed class StartLiveSessionCommandHandler(
    IStrategyFactory strategyFactory,
    ILiveAccountManager accountManager,
    ILiveSessionStore sessionStore) : ICommandHandler<StartLiveSessionCommand, LiveSessionSubmissionDto>
{
    public async Task<LiveSessionSubmissionDto> HandleAsync(StartLiveSessionCommand command, CancellationToken ct = default)
    {
        var strategy = strategyFactory.Create(
            command.StrategyName,
            PassthroughIndicatorFactory.Instance,
            command.StrategyParameters);

        var subscriptions = strategy.DataSubscriptions;
        if (subscriptions.Count == 0)
            throw new ArgumentException("Strategy must define at least one data subscription.");

        var primaryAsset = subscriptions[0].Asset;
        var sessionId = Guid.NewGuid();
        var initialCashScaled = (long)(command.InitialCash / primaryAsset.TickSize);
        var commissionScaled = (long)(command.CommissionPerTrade / primaryAsset.TickSize);

        var config = new LiveSessionConfig
        {
            SessionId = sessionId,
            Strategy = strategy,
            Subscriptions = subscriptions,
            PrimaryAsset = primaryAsset,
            InitialCash = initialCashScaled,
            CommissionPerTrade = commissionScaled,
            Routing = command.Routing,
            AccountName = command.AccountName,
        };

        var connector = await accountManager.GetOrCreateAsync(command.AccountName, ct);
        await connector.AddSessionAsync(config, ct);
        sessionStore.Add(sessionId, command.AccountName, connector);

        return new LiveSessionSubmissionDto { SessionId = sessionId };
    }
}
