using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.LiveHost.Application.Live;

public sealed class StartLiveSessionCommandHandler(
    IStrategyFactory strategyFactory,
    ILiveAccountManager accountManager,
    ILiveSessionStore sessionStore,
    IAssetRepository assetRepository,
    IOptimizationSpaceProvider spaceProvider) : ICommandHandler<StartLiveSessionCommand, LiveSessionSubmissionDto>
{
    public async Task<LiveSessionSubmissionDto> HandleAsync(StartLiveSessionCommand command, CancellationToken ct = default)
    {
        if (command.DataSubscriptions is null or { Count: 0 })
            throw new ArgumentException("At least one data subscription must be provided.");

        // Live trading is TimeBar-only: the connector aggregator pipeline doesn't yet emit
        // alt-bars in real time. Silently coercing a non-TimeBar primary to 1m would deliver
        // wrong data, so reject explicitly.
        var resolvedSubscriptions = new List<DataSubscription>();
        foreach (var sub in command.DataSubscriptions)
        {
            var asset = await assetRepository.GetByNameAsync(sub.AssetName, sub.Exchange, ct)
                ?? throw new ArgumentException($"Asset '{sub.AssetName}' on exchange '{sub.Exchange}' not found.");

            if (sub is not TimeBarSubscription tb)
                throw new NotSupportedException(
                    $"Live trading currently supports TimeBarSubscription only; got {sub.GetType().Name}. " +
                    "Alt-bar / tick primaries are supported for backtest and optimization only.");

            resolvedSubscriptions.Add(new DataSubscription(asset, tb.TimeFrame));
        }

        var primaryAsset = resolvedSubscriptions[0].Asset;

        // Scale QuoteAsset strategy params from human-readable to tick units
        var scale = new ScaleContext(primaryAsset);
        var scaledParams = ParameterScaler.ScaleQuoteAssetParams(
            spaceProvider, command.StrategyName, command.StrategyParameters, scale);

        var strategy = strategyFactory.Create(
            command.StrategyName,
            PassthroughIndicatorFactory.Instance,
            scaledParams);

        // Add subscriptions to strategy (like BacktestPreparer)
        if (strategy.DataSubscriptions.Count == 0)
        {
            foreach (var sub in resolvedSubscriptions)
                strategy.DataSubscriptions.Add(sub);
        }

        var subscriptions = strategy.DataSubscriptions;

        var fingerprint = LiveRunKeyBuilder.Build(command);

        var sessionId = Guid.NewGuid();
        var initialCashScaled = scale.AmountToTicks(command.InitialCash);

        var config = new LiveSessionConfig
        {
            SessionId = sessionId,
            Strategy = strategy,
            Subscriptions = subscriptions,
            PrimaryAsset = primaryAsset,
            InitialCash = initialCashScaled,
            Routing = command.Routing,
            AccountName = command.AccountName,
        };

        var exchange = subscriptions[0].Asset.Exchange;
        var connector = await accountManager.GetOrCreateAsync(command.AccountName, ct);

        var details = new SessionDetails(
            command.AccountName,
            connector,
            command.StrategyName,
            strategy.Version,
            exchange,
            primaryAsset.Name,
            fingerprint,
            DateTimeOffset.UtcNow);

        if (!sessionStore.TryAdd(sessionId, details))
        {
            throw new InvalidOperationException(
                $"A live session with the same strategy configuration is already running " +
                $"(strategy={command.StrategyName}, version={strategy.Version}).");
        }

        try
        {
            await connector.AddSessionAsync(config, ct);
        }
        catch
        {
            sessionStore.Remove(sessionId);
            throw;
        }

        return new LiveSessionSubmissionDto { SessionId = sessionId };
    }
}
