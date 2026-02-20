using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Reporting;

namespace AlgoTradeForge.Application.Backtests;

public sealed class RunBacktestCommandHandler(
    BacktestEngine engine,
    BacktestPreparer preparer,
    IMetricsCalculator metricsCalculator) : ICommandHandler<RunBacktestCommand, BacktestResultDto>
{
    public async Task<BacktestResultDto> HandleAsync(RunBacktestCommand command, CancellationToken ct = default)
    {
        var setup = await preparer.PrepareAsync(command, PassthroughIndicatorFactory.Instance, ct);

        var result = engine.Run(setup.Series, setup.Strategy, setup.Options, ct);

        var metrics = metricsCalculator.Calculate(
            result.Fills, result.EquityCurve, setup.Options.InitialCash,
            command.StartTime, command.EndTime);

        return new BacktestResultDto
        {
            Id = Guid.NewGuid(),
            AssetName = command.AssetName,
            StrategyName = command.StrategyName,
            InitialCapital = metrics.InitialCapital / setup.ScaleFactor,
            FinalEquity = metrics.FinalEquity / setup.ScaleFactor,
            NetProfit = metrics.NetProfit / setup.ScaleFactor,
            TotalCommissions = metrics.TotalCommissions / setup.ScaleFactor,
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
