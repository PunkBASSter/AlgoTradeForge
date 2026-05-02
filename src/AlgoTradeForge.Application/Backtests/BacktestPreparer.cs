using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
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
            for (var i = 0; i < command.DataSubscriptions.Count; i++)
            {
                var sub = command.DataSubscriptions[i];
                var subAsset = i == 0
                    ? asset
                    : await assetRepository.GetByNameAsync(sub.AssetName, sub.Exchange, ct)
                      ?? throw new ArgumentException($"Asset '{sub.AssetName}' not found.");

                // Phase 4 PR-A: backtest engine path is TimeBar-only; alt-bar / tick / side
                // primaries arrive via PR-C once HistoryRepository.Load(DataFeedSubscription)
                // is wired. Mirrors the guards in OptimizationSetupHelper / StartLiveSession
                // — silent coercion to a 1m time bar would load the wrong feed and produce
                // a misleading run record.
                if (sub is not TimeBarSubscription tb)
                    throw new NotSupportedException(
                        $"Phase 4 PR-A: only TimeBarSubscription is supported in BacktestPreparer; " +
                        $"got {sub.GetType().Name}. PR-C extends this to alt-bar / tick / side primaries.");

                strategy.DataSubscriptions.Add(new DataSubscription(subAsset, tb.TimeFrame));
            }
        }

        var fromDate = DateOnly.FromDateTime(settings.StartTime.UtcDateTime);
        var toDate = DateOnly.FromDateTime(settings.EndTime.UtcDateTime);

        var seriesArray = new TimeSeries<Int64Bar>[strategy.DataSubscriptions.Count];
        for (var i = 0; i < strategy.DataSubscriptions.Count; i++)
        {
            seriesArray[i] = historyRepository.Load(strategy.DataSubscriptions[i], fromDate, toDate);
        }

        // Phase 4 (TRD §9.3) — propagate the primary's feed-id so FeedContextBuilder can
        // lazy-bind a sidecar if `feeds.json` lists one. TimeBar primaries pass their
        // canonical TimeFrame.Code; AltBar primaries pass their FeedId. Tick/Side never
        // sidecar (they are themselves the source / a side feed).
        var primaryTimeFrameCode = primarySub switch
        {
            TimeBarSubscription tb => tb.TimeFrame.Code,
            AltBarSubscription ab => ab.FeedId,
            _ => null,
        };

        var feedContext = feedContextBuilder?.Build(
            storageOptions?.Value.DataRoot ?? CandleStorageOptions.DefaultDataRoot,
            asset, fromDate, toDate, primaryFeedName: primaryTimeFrameCode);

        return new BacktestSetup(asset, scale, options, strategy, seriesArray, FeedContext: feedContext);
    }
}
