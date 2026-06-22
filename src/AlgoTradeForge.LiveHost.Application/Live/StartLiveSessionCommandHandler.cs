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

        // Resolve each typed subscription 1:1, same-order, into a DataSubscription. Alt-bar/tick
        // identity is carried by FeedKey (alt-bar feed-id or "tick"); the data plane pairs the
        // resolved list with command.DataSubscriptions positionally — they must stay equal-length.
        var resolvedSubscriptions = new List<DataSubscription>(command.DataSubscriptions.Count);
        foreach (var sub in command.DataSubscriptions)
        {
            var asset = await assetRepository.GetByNameAsync(sub.AssetName, sub.Exchange, ct)
                ?? throw new ArgumentException($"Asset '{sub.AssetName}' on exchange '{sub.Exchange}' not found.");

            DataSubscription resolved = sub switch
            {
                TimeBarSubscription tb => new DataSubscription(asset, tb.TimeFrame),
                AltBarSubscription ab => new DataSubscription(asset, default, FeedKey: ab.FeedId),
                TickSubscription => new DataSubscription(asset, default, FeedKey: "tick"),
                _ => throw new NotSupportedException(
                    $"Unsupported live subscription kind: {sub.GetType().Name}"),
            };
            resolvedSubscriptions.Add(resolved);
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

        // config.Subscriptions MUST stay 1:1 same-order with RawSubscriptions (data-plane pairing),
        // so use the resolved list — not strategy.DataSubscriptions, which a strategy may pre-populate.
        var fingerprint = LiveRunKeyBuilder.Build(command);

        var sessionId = Guid.NewGuid();
        var initialCashScaled = scale.AmountToTicks(command.InitialCash);

        var config = new LiveSessionConfig
        {
            SessionId = sessionId,
            Strategy = strategy,
            Subscriptions = resolvedSubscriptions,
            RawSubscriptions = command.DataSubscriptions,
            PrimaryAsset = primaryAsset,
            InitialCash = initialCashScaled,
            AccountName = command.AccountName,
        };

        var exchange = primaryAsset.Exchange;
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
