using System.Globalization;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Engine;
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
        var sub = command.DataSubscription;
        var settings = command.BacktestSettings;

        var asset = await assetRepository.GetByNameAsync(sub.AssetName, sub.Exchange, ct)
            ?? throw new ArgumentException($"Asset '{sub.AssetName}' not found.", nameof(command));

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
            TimeSpan timeFrame;
            if (string.IsNullOrEmpty(sub.TimeFrame))
            {
                timeFrame = TimeSpan.FromMinutes(1);
            }
            else if (!TimeSpan.TryParse(sub.TimeFrame, CultureInfo.InvariantCulture, out timeFrame))
            {
                throw new ArgumentException($"Invalid TimeFrame format: '{sub.TimeFrame}'");
            }

            strategy.DataSubscriptions.Add(new DataSubscription(asset, timeFrame));
        }

        var fromDate = DateOnly.FromDateTime(settings.StartTime.UtcDateTime);
        var toDate = DateOnly.FromDateTime(settings.EndTime.UtcDateTime);

        var seriesArray = new TimeSeries<Int64Bar>[strategy.DataSubscriptions.Count];
        for (var i = 0; i < strategy.DataSubscriptions.Count; i++)
        {
            seriesArray[i] = historyRepository.Load(strategy.DataSubscriptions[i], fromDate, toDate);
        }

        var feedContext = feedContextBuilder?.Build(
            storageOptions?.Value.DataRoot ?? CandleStorageOptions.DefaultDataRoot,
            asset, fromDate, toDate);

        return new BacktestSetup(asset, scale, options, strategy, seriesArray, FeedContext: feedContext);
    }
}
