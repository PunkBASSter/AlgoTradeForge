using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Application.Backtests;

public sealed class RunBacktestCommandHandler(
    BacktestEngine engine,
    IAssetRepository assetRepository,
    IStrategyFactory strategyFactory,
    IDataSource dataSource) : ICommandHandler<RunBacktestCommand, BacktestResultDto>
{
    public async Task<BacktestResultDto> HandleAsync(RunBacktestCommand command, CancellationToken ct = default)
    {
        var asset = await assetRepository.GetByNameAsync(command.AssetName, ct)
            ?? throw new ArgumentException($"Asset '{command.AssetName}' not found.", nameof(command));

        var strategy = strategyFactory.Create(command.StrategyName, command.StrategyParameters);

        var options = new BacktestOptions
        {
            Asset = asset,
            InitialCash = command.InitialCash,
            StartTime = command.StartTime,
            EndTime = command.EndTime,
            CommissionPerTrade = command.CommissionPerTrade,
            SlippageTicks = command.SlippageTicks
        };

        var subscriptions = strategy.DataSubscriptions.Count > 0
            ? strategy.DataSubscriptions
            : (IList<DataSubscription>)[new DataSubscription(asset, asset.SmallestInterval)];

        var dataMap = new Dictionary<DataSubscription, TimeSeries<Int64Bar>>();
        foreach (var sub in subscriptions)
        {
            var query = new HistoryDataQuery
            {
                Asset = sub.Asset,
                TimeFrame = sub.TimeFrame,
                StartTime = command.StartTime,
                EndTime = command.EndTime
            };
            dataMap[sub] = dataSource.GetData(query);
        }

        var snapshot = new MarketDataSnapshot(dataMap);

        var primarySubscription = subscriptions
            .Where(s => s.Asset == asset)
            .OrderBy(s => s.TimeFrame)
            .First();

        var result = await engine.RunAsync(snapshot[primarySubscription], strategy, options, ct);

        return new BacktestResultDto
        {
            Id = Guid.NewGuid(),
            AssetName = command.AssetName,
            StrategyName = command.StrategyName,
            InitialCapital = result.Metrics.InitialCapital,
            FinalEquity = result.Metrics.FinalEquity,
            NetProfit = result.Metrics.NetProfit,
            TotalReturnPct = result.Metrics.TotalReturnPct,
            AnnualizedReturnPct = result.Metrics.AnnualizedReturnPct,
            SharpeRatio = result.Metrics.SharpeRatio,
            SortinoRatio = result.Metrics.SortinoRatio,
            MaxDrawdownPct = result.Metrics.MaxDrawdownPct,
            TotalTrades = result.Metrics.TotalTrades,
            WinRatePct = result.Metrics.WinRatePct,
            ProfitFactor = result.Metrics.ProfitFactor,
            TradingDays = result.Metrics.TradingDays,
            Duration = result.Duration,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }
}
