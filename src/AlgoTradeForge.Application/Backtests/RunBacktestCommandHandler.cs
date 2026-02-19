using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Application.Backtests;

public sealed class RunBacktestCommandHandler(
    BacktestEngine engine,
    IAssetRepository assetRepository,
    IStrategyFactory strategyFactory,
    IHistoryRepository historyRepository,
    IMetricsCalculator metricsCalculator) : ICommandHandler<RunBacktestCommand, BacktestResultDto>
{
    public async Task<BacktestResultDto> HandleAsync(RunBacktestCommand command, CancellationToken ct = default)
    {
        var asset = await assetRepository.GetByNameAsync(command.AssetName, command.Exchange, ct)
            ?? throw new ArgumentException($"Asset '{command.AssetName}' not found.", nameof(command));

        var strategy = strategyFactory.Create(command.StrategyName, command.StrategyParameters);

        var options = new BacktestOptions
        {
            InitialCash = command.InitialCash,
            Asset = asset,
            StartTime = command.StartTime,
            EndTime = command.EndTime,
            CommissionPerTrade = command.CommissionPerTrade,
            SlippageTicks = command.SlippageTicks,
            UseDetailedExecutionLogic = command.UseDetailedExecutionLogic
        };

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

        var result = engine.Run(seriesArray, strategy, options, ct);

        var metrics = metricsCalculator.Calculate(
            result.Fills, result.EquityCurve, command.InitialCash,
            command.StartTime, command.EndTime);

        return new BacktestResultDto
        {
            Id = Guid.NewGuid(),
            AssetName = command.AssetName,
            StrategyName = command.StrategyName,
            InitialCapital = metrics.InitialCapital,
            FinalEquity = metrics.FinalEquity,
            NetProfit = metrics.NetProfit,
            TotalReturnPct = metrics.TotalReturnPct,
            AnnualizedReturnPct = metrics.AnnualizedReturnPct,
            SharpeRatio = metrics.SharpeRatio,
            SortinoRatio = metrics.SortinoRatio,
            MaxDrawdownPct = metrics.MaxDrawdownPct,
            TotalTrades = metrics.TotalTrades,
            WinRatePct = metrics.WinRatePct,
            ProfitFactor = metrics.ProfitFactor,
            TradingDays = metrics.TradingDays,
            Duration = result.Duration,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }
}
