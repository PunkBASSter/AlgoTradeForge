using System.Globalization;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Application.Live;

public sealed class StartLiveSessionCommandHandler(
    IAssetRepository assetRepository,
    IStrategyFactory strategyFactory,
    ILiveConnector liveConnector,
    ILiveSessionStore sessionStore) : ICommandHandler<StartLiveSessionCommand, LiveSessionSubmissionDto>
{
    public async Task<LiveSessionSubmissionDto> HandleAsync(StartLiveSessionCommand command, CancellationToken ct = default)
    {
        var asset = await assetRepository.GetByNameAsync(command.AssetName, command.Exchange, ct)
                    ?? throw new ArgumentException($"Asset '{command.AssetName}' not found on exchange '{command.Exchange}'.");

        var strategy = strategyFactory.Create(
            command.StrategyName,
            PassthroughIndicatorFactory.Instance,
            command.StrategyParameters);

        TimeSpan timeFrame = TimeSpan.FromMinutes(1);
        if (command.TimeFrame is not null)
        {
            if (!TimeSpan.TryParse(command.TimeFrame, CultureInfo.InvariantCulture, out var parsed))
                throw new ArgumentException($"Invalid TimeFrame '{command.TimeFrame}'.");
            timeFrame = parsed;
        }

        var subscriptions = strategy.DataSubscriptions;
        if (subscriptions.Count == 0)
            subscriptions = [new DataSubscription(asset, timeFrame)];

        var sessionId = Guid.NewGuid();
        var commissionScaled = (long)(command.CommissionPerTrade / asset.TickSize);

        var config = new LiveSessionConfig
        {
            SessionId = sessionId,
            Strategy = strategy,
            Subscriptions = subscriptions,
            PrimaryAsset = asset,
            InitialCash = command.InitialCash,
            CommissionPerTrade = commissionScaled,
            Routing = command.Routing,
            PaperTrading = command.PaperTrading,
        };

        await liveConnector.StartAsync(config, ct);
        sessionStore.Add(sessionId, liveConnector);

        return new LiveSessionSubmissionDto { SessionId = sessionId };
    }
}
