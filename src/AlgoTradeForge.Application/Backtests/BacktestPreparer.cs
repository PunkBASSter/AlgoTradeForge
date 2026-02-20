using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Application.Backtests;

public sealed class BacktestPreparer(
    IAssetRepository assetRepository,
    IStrategyFactory strategyFactory,
    IHistoryRepository historyRepository)
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
        var asset = await assetRepository.GetByNameAsync(command.AssetName, command.Exchange, ct)
            ?? throw new ArgumentException($"Asset '{command.AssetName}' not found.", nameof(command));

        var scaleFactor = 1m / asset.TickSize;

        var options = new BacktestOptions
        {
            InitialCash = (long)(command.InitialCash * scaleFactor),
            Asset = asset,
            StartTime = command.StartTime,
            EndTime = command.EndTime,
            CommissionPerTrade = (long)(command.CommissionPerTrade * scaleFactor),
            SlippageTicks = command.SlippageTicks,
            UseDetailedExecutionLogic = command.UseDetailedExecutionLogic
        };

        var indicatorFactory = indicatorFactoryProvider(options);
        var strategy = strategyFactory.Create(command.StrategyName, indicatorFactory, command.StrategyParameters);

        if (strategy.DataSubscriptions.Count == 0)
        {
            var timeFrame = command.TimeFrame ?? asset.SmallestInterval;
            strategy.DataSubscriptions.Add(new DataSubscription(asset, timeFrame));
        }

        var fromDate = DateOnly.FromDateTime(command.StartTime.UtcDateTime);
        var toDate = DateOnly.FromDateTime(command.EndTime.UtcDateTime);

        var seriesArray = new TimeSeries<Int64Bar>[strategy.DataSubscriptions.Count];
        for (var i = 0; i < strategy.DataSubscriptions.Count; i++)
        {
            seriesArray[i] = historyRepository.Load(strategy.DataSubscriptions[i], fromDate, toDate);
        }

        return new BacktestSetup(asset, scaleFactor, options, strategy, seriesArray);
    }
}
