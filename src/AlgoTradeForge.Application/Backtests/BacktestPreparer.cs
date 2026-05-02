using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Strategy;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Application.Backtests;

public sealed class BacktestPreparer(
    IAssetRepository assetRepository,
    IStrategyFactory strategyFactory,
    IHistoryRepository historyRepository,
    IOptimizationSpaceProvider spaceProvider,
    IOptions<CandleStorageOptions>? storageOptions = null,
    IFeedContextBuilder? feedContextBuilder = null)
{
    public Task<BacktestSetup> PrepareAsync(
        IBacktestSetupCommand command,
        IIndicatorFactory indicatorFactory,
        CancellationToken ct = default)
        => PrepareAsync(command, _ => indicatorFactory, ct);

    public async Task<BacktestSetup> PrepareAsync(
        IBacktestSetupCommand command,
        Func<BacktestOptions, IIndicatorFactory> indicatorFactoryProvider,
        CancellationToken ct = default)
    {
        var primarySub = command.DataSubscriptions[0];
        var settings = command.BacktestSettings;

        var asset = await assetRepository.GetByNameAsync(primarySub.AssetName, primarySub.Exchange, ct)
            ?? throw new ArgumentException($"Asset '{primarySub.AssetName}' not found.", nameof(command));

        var scale = new ScaleContext(asset);

        var options = new BacktestOptions
        {
            InitialCash = scale.AmountToTicks(settings.InitialCash),
            StartTime = settings.StartTime,
            EndTime = settings.EndTime,
            CommissionPerTrade = settings.CommissionPerTrade,
            SlippageTicks = settings.SlippageTicks,
            UseDetailedExecutionLogic = command.UseDetailedExecutionLogic
        };

        var indicatorFactory = indicatorFactoryProvider(options);
        var scaledParams = ParameterScaler.ScaleQuoteAssetParams(
            spaceProvider, command.StrategyName, command.StrategyParameters, scale);
        var strategy = strategyFactory.Create(command.StrategyName, indicatorFactory, scaledParams);

        if (strategy.DataSubscriptions.Count == 0)
        {
            foreach (var sub in command.DataSubscriptions)
            {
                var subAsset = sub == primarySub
                    ? asset
                    : await assetRepository.GetByNameAsync(sub.AssetName, sub.Exchange, ct)
                      ?? throw new ArgumentException($"Asset '{sub.AssetName}' not found.");

                TimeFrame timeFrame;
                if (string.IsNullOrEmpty(sub.TimeFrame))
                {
                    timeFrame = new TimeFrame(TimeSpan.FromMinutes(1));
                }
                else if (!TimeFrame.TryParseLiberal(sub.TimeFrame, out timeFrame))
                {
                    throw new ArgumentException($"Invalid TimeFrame format: '{sub.TimeFrame}'");
                }

                strategy.DataSubscriptions.Add(new DataSubscription(subAsset, timeFrame));
            }
        }

        var fromDate = DateOnly.FromDateTime(settings.StartTime.UtcDateTime);
        var toDate = DateOnly.FromDateTime(settings.EndTime.UtcDateTime);

        var seriesArray = new TimeSeries<Int64Bar>[strategy.DataSubscriptions.Count];
        for (var i = 0; i < strategy.DataSubscriptions.Count; i++)
        {
            seriesArray[i] = historyRepository.Load(strategy.DataSubscriptions[i], fromDate, toDate);
        }

        // Phase 2b — pass the primary's feed-id (the time-frame code, e.g. "1m") so the
        // builder can lazy-bind a primary sidecar if `feeds.json` lists one. For Phase 2b's
        // pre-Phase-4 callers, primary is always a TimeBar and time-bar feeds don't carry
        // sidecars; the binding is a no-op until Phase 4 lands AltBar primaries.
        // Preserve only canonical-shorthand inputs verbatim — the feed-id grammar (TRD §3.3)
        // requires the lowercase form, and `feeds.json` directories are named with it. A
        // wire-form input ("00:01:00") would still be a valid TimeFrame but isn't a feed-id;
        // bind only when the request payload itself was already in shorthand.
        var primaryTimeFrameCode = TimeFrame.TryParse(primarySub.TimeFrame, out _)
            ? primarySub.TimeFrame
            : null;

        var feedContext = feedContextBuilder?.Build(
            storageOptions?.Value.DataRoot ?? CandleStorageOptions.DefaultDataRoot,
            asset, fromDate, toDate, primaryFeedName: primaryTimeFrameCode);

        return new BacktestSetup(asset, scale, options, strategy, seriesArray, FeedContext: feedContext);
    }
}
