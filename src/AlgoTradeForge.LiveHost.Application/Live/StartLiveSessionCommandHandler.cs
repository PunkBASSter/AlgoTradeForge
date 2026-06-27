using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Application.Collection;

namespace AlgoTradeForge.LiveHost.Application.Live;

public sealed class StartLiveSessionCommandHandler(
    IStrategyFactory strategyFactory,
    ILiveAccountManager accountManager,
    ILiveSessionStore sessionStore,
    IAssetRepository assetRepository,
    IOptimizationSpaceProvider spaceProvider,
    ICollectionConfigStore collectionStore) : ICommandHandler<StartLiveSessionCommand, LiveSessionSubmissionDto>
{
    public async Task<LiveSessionSubmissionDto> HandleAsync(StartLiveSessionCommand command, CancellationToken ct = default)
    {
        if (command.DataSubscriptions is null or { Count: 0 })
            throw new ArgumentException("At least one data subscription must be provided.");

        // Resolve each typed subscription 1:1, same-order, into a DataFeedSubscription. Alt-bar/tick
        // identity is carried by FeedKey (alt-bar feed-id or "tick"); the data plane pairs the
        // resolved list with command.DataSubscriptions positionally — they must stay equal-length.
        var resolvedSubscriptions = new List<DataFeedSubscription>(command.DataSubscriptions.Count);
        foreach (var sub in command.DataSubscriptions)
        {
            var asset = await assetRepository.GetByNameAsync(sub.AssetName, sub.Exchange, ct)
                ?? throw new ArgumentException($"Asset '{sub.AssetName}' on exchange '{sub.Exchange}' not found.");

            resolvedSubscriptions.Add(SubscriptionResolver.Resolve(sub, asset));
        }

        var collected = await collectionStore.Load(ct);
        var unmet = CollectionCoverage.FindUnmet(collected.Config.Feeds, resolvedSubscriptions);
        if (unmet is not null)
            throw new ArgumentException($"Cannot execute on uncollected feed: {unmet}. Add it to collection.json first.");

        var executionAsset = resolvedSubscriptions.ResolveExecutionAsset();

        // Scale QuoteAsset strategy params from human-readable to tick units
        var scale = new ScaleContext(executionAsset);
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

        var fingerprint = LiveRunKeyBuilder.Build(command);

        var sessionId = Guid.NewGuid();

        var config = new LiveSessionConfig
        {
            SessionId = sessionId,
            Strategy = strategy,
            Subscriptions = resolvedSubscriptions,
            AccountName = command.AccountName,
        };

        var exchange = executionAsset.Exchange;
        var connector = await accountManager.GetOrCreateAsync(command.AccountName, ct);

        var details = new SessionDetails(
            command.AccountName,
            connector,
            command.StrategyName,
            strategy.Version,
            exchange,
            executionAsset.Name,
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
