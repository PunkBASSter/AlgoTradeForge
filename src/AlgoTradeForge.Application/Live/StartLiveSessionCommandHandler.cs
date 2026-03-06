using System.Globalization;
using System.Text.Json;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Strategy;

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

        // Resolve assets from subscription DTOs
        var resolvedSubscriptions = new List<DataSubscription>();
        foreach (var sub in command.DataSubscriptions)
        {
            var asset = await assetRepository.GetByNameAsync(sub.Asset, sub.Exchange, ct)
                ?? throw new ArgumentException($"Asset '{sub.Asset}' on exchange '{sub.Exchange}' not found.");

            if (!TimeSpan.TryParse(sub.TimeFrame, CultureInfo.InvariantCulture, out var timeFrame))
                throw new ArgumentException($"Invalid TimeFrame '{sub.TimeFrame}' for asset '{sub.Asset}'.");

            resolvedSubscriptions.Add(new DataSubscription(asset, timeFrame));
        }

        var primaryAsset = resolvedSubscriptions[0].Asset;

        // Scale QuoteAsset strategy params from human-readable to tick units
        var scaledParams = ScaleQuoteAssetParams(command.StrategyName, command.StrategyParameters, primaryAsset);

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
        var initialCashScaled = (long)(command.InitialCash / primaryAsset.TickSize);

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

    private IDictionary<string, object>? ScaleQuoteAssetParams(
        string strategyName, IDictionary<string, object>? parameters, Asset primaryAsset)
    {
        if (parameters is null or { Count: 0 })
            return parameters;

        var descriptor = spaceProvider.GetDescriptor(strategyName);
        if (descriptor is null)
            return parameters;

        var scaleFactor = 1m / primaryAsset.TickSize;
        var scaled = new Dictionary<string, object>(parameters);

        foreach (var axis in descriptor.Axes.OfType<NumericRangeAxis>())
        {
            if (axis.Unit != ParamUnit.QuoteAsset)
                continue;

            if (!scaled.TryGetValue(axis.Name, out var value))
                continue;

            var decimalValue = value is JsonElement je
                ? je.GetDecimal()
                : Convert.ToDecimal(value);
            scaled[axis.Name] = (long)(decimalValue * scaleFactor);
        }

        return scaled;
    }
}
