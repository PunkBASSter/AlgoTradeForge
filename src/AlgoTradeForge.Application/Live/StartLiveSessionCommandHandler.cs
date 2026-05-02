using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.Application.Live;

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

        // Resolve assets from subscriptions. Phase 4 (P4-12) lifts the backtest+optimization
        // guards but live trading stays TimeBar-only: alt-bar / tick live trading needs the
        // connector aggregator pipeline to emit alt-bars in real time, which is post-Phase-6
        // territory. Silently coercing a non-TimeBar primary to a 1m time bar would deliver
        // wrong data to the live session.
        var resolvedSubscriptions = new List<DataSubscription>();
        foreach (var sub in command.DataSubscriptions)
        {
            var asset = await assetRepository.GetByNameAsync(sub.AssetName, sub.Exchange, ct)
                ?? throw new ArgumentException($"Asset '{sub.AssetName}' on exchange '{sub.Exchange}' not found.");

            if (sub is not TimeBarSubscription tb)
                throw new NotSupportedException(
                    $"Live trading currently supports TimeBarSubscription only; got {sub.GetType().Name}. " +
                    "Alt-bar / tick live trading requires the live data pipeline to emit alt-bars in real " +
                    "time (post-Phase-6 — the connector aggregator side is not built yet). Use a TimeBar " +
                    "primary for live runs; alt-bar primaries are supported for backtest + optimization only.");

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

        var fingerprint = RunKeyBuilder.Build(command);

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
