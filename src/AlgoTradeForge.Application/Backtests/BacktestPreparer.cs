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

        var fromDate = DateOnly.FromDateTime(settings.StartTime.UtcDateTime);
        var toDate = DateOnly.FromDateTime(settings.EndTime.UtcDateTime);

        TimeSeries<Int64Bar>[] seriesArray;

        if (strategy.DataSubscriptions.Count == 0)
        {
            // Side entries are bound via FeedContextBuilder below; they are FeedSeries, not
            // TimeSeries<Int64Bar>, so they don't enter strategy.DataSubscriptions/seriesArray.
            var primaries = new List<(DataFeedSubscription FeedSub, Asset SubAsset, DataSubscription StrategySub)>();
            for (var i = 0; i < command.DataSubscriptions.Count; i++)
            {
                var sub = command.DataSubscriptions[i];
                if (sub.Role == DataFeedRole.Side) continue;

                var subAsset = i == 0
                    ? asset
                    : await assetRepository.GetByNameAsync(sub.AssetName, sub.Exchange, ct)
                      ?? throw new ArgumentException($"Asset '{sub.AssetName}' not found.");

                var strategySub = StrategySubscriptionFactory.FromPrimary(sub, subAsset);
                primaries.Add((sub, subAsset, strategySub));
            }

            seriesArray = new TimeSeries<Int64Bar>[primaries.Count];
            for (var i = 0; i < primaries.Count; i++)
            {
                strategy.DataSubscriptions.Add(primaries[i].StrategySub);
                seriesArray[i] = historyRepository.Load(
                    primaries[i].SubAsset, primaries[i].FeedSub, fromDate, toDate);
            }
        }
        else
        {
            // Pre-declared subscriptions: legacy Load(DataSubscription, ...) only knows TimeBar.
            // Reject non-TimeBar FeedKeys to prevent silent coercion to a 1m load.
            seriesArray = new TimeSeries<Int64Bar>[strategy.DataSubscriptions.Count];
            for (var i = 0; i < strategy.DataSubscriptions.Count; i++)
            {
                var preDeclared = strategy.DataSubscriptions[i];
                if (preDeclared.FeedKey != "ohlcv")
                    throw new NotSupportedException(
                        $"Strategy pre-declared a non-TimeBar subscription (FeedKey='{preDeclared.FeedKey}'). " +
                        "Strategies must declare alt-bar / tick / side primaries via the command's " +
                        "DataSubscriptions (DataFeedSubscription), not strategy.DataSubscriptions.");
                seriesArray[i] = historyRepository.Load(preDeclared, fromDate, toDate);
            }
        }

        for (var i = 0; i < seriesArray.Length; i++)
        {
            if (seriesArray[i].Count > 0) continue;
            var sub = command.DataSubscriptions[i];
            var feedDescriptor = sub switch
            {
                TimeBarSubscription tb => $"TimeBar timeFrame='{tb.TimeFrame.Code}'",
                AltBarSubscription ab => $"AltBar feedId='{ab.FeedId}'",
                TickSubscription => "Tick",
                _ => sub.GetType().Name,
            };
            throw new ArgumentException(
                $"Data feed produced 0 bars for {feedDescriptor} on {sub.AssetName}@{sub.Exchange} " +
                $"in range {fromDate:yyyy-MM-dd}..{toDate:yyyy-MM-dd}. " +
                $"Verify the feed exists on disk and contains data for the requested period.",
                nameof(command));
        }

        // Propagate the primary's feed-id so FeedContextBuilder can lazy-bind a sidecar from
        // feeds.json. Tick/Side never sidecar (they are themselves source / side).
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
