using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain.Engine;

namespace AlgoTradeForge.Application.Backtests;

public sealed class RunBacktestCommandHandler : ICommandHandler<RunBacktestCommand, BacktestResultDto>
{
    private readonly BacktestEngine _engine;
    private readonly IAssetRepository _assetRepository;
    private readonly IBarSourceRepository _barSourceRepository;
    private readonly IStrategyFactory _strategyFactory;

    public RunBacktestCommandHandler(
        BacktestEngine engine,
        IAssetRepository assetRepository,
        IBarSourceRepository barSourceRepository,
        IStrategyFactory strategyFactory)
    {
        _engine = engine;
        _assetRepository = assetRepository;
        _barSourceRepository = barSourceRepository;
        _strategyFactory = strategyFactory;
    }

    public async Task<BacktestResultDto> HandleAsync(RunBacktestCommand command, CancellationToken ct = default)
    {
        var asset = await _assetRepository.GetByNameAsync(command.AssetName, ct)
            ?? throw new ArgumentException($"Asset '{command.AssetName}' not found.", nameof(command));

        var barSource = await _barSourceRepository.GetByNameAsync(command.BarSourceName, ct)
            ?? throw new ArgumentException($"Bar source '{command.BarSourceName}' not found.", nameof(command));

        var strategy = _strategyFactory.Create(command.StrategyName, command.StrategyParameters);

        var options = new BacktestOptions
        {
            Asset = asset,
            InitialCash = command.InitialCash,
            StartTime = command.StartTime,
            EndTime = command.EndTime,
            CommissionPerTrade = command.CommissionPerTrade,
            SlippageTicks = command.SlippageTicks
        };

        var result = await _engine.RunAsync(barSource, strategy, options, ct);

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
