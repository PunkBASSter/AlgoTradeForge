using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain.Engine;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Application.Backtests;

public sealed class RunBacktestCommandHandler(
    BacktestEngine engine,
    IAssetRepository assetRepository,
    IStrategyFactory strategyFactory,
    IInt64BarLoader barLoader,
    IOptions<CandleStorageOptions> storageOptions) : ICommandHandler<RunBacktestCommand, BacktestResultDto>
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

        var bars = barLoader.Load(
            storageOptions.Value.DataRoot,
            asset.Exchange ?? throw new InvalidOperationException($"Asset '{asset.Name}' has no Exchange configured."),
            asset.Name,
            asset.DecimalDigits,
            DateOnly.FromDateTime(command.StartTime.UtcDateTime),
            DateOnly.FromDateTime(command.EndTime.UtcDateTime),
            asset.SmallestInterval);

        var result = await engine.RunAsync(bars, strategy, options, ct);

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
