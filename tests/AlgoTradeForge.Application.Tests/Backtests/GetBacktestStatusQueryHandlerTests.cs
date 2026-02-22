using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Backtests;

public class GetBacktestStatusQueryHandlerTests
{
    private readonly RunProgressCache _progressCache;
    private readonly IRunRepository _repository = Substitute.For<IRunRepository>();
    private readonly GetBacktestStatusQueryHandler _handler;

    public GetBacktestStatusQueryHandlerTests()
    {
        var distributedCache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        _progressCache = new RunProgressCache(distributedCache);
        _handler = new GetBacktestStatusQueryHandler(_progressCache, _repository);
    }

    [Fact]
    public async Task HandleAsync_ActiveRun_ReturnsRunningWithNoResult()
    {
        var id = Guid.NewGuid();
        await _progressCache.SetProgressAsync(id, 50, 100);

        var dto = await _handler.HandleAsync(new GetBacktestStatusQuery(id));

        Assert.NotNull(dto);
        Assert.Equal(id, dto.Id);
        Assert.Null(dto.Result);
    }

    [Fact]
    public async Task HandleAsync_CompletedRun_ReturnsWithResult()
    {
        var id = Guid.NewGuid();
        var record = new BacktestRunRecord
        {
            Id = id, StrategyName = "S", StrategyVersion = "1",
            Parameters = new Dictionary<string, object>(),
            AssetName = "BTC", Exchange = "Binance", TimeFrame = "00:01:00",
            InitialCash = 10_000m, Commission = 0m, SlippageTicks = 0,
            StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
            DataStart = DateTimeOffset.UtcNow, DataEnd = DateTimeOffset.UtcNow,
            DurationMs = 100, TotalBars = 10,
            Metrics = new Domain.Reporting.PerformanceMetrics
            {
                TotalTrades = 0, WinningTrades = 0, LosingTrades = 0,
                NetProfit = 0, GrossProfit = 0, GrossLoss = 0, TotalCommissions = 0,
                TotalReturnPct = 0, AnnualizedReturnPct = 0,
                SharpeRatio = 0, SortinoRatio = 0, MaxDrawdownPct = 0,
                WinRatePct = 0, ProfitFactor = 0, AverageWin = 0, AverageLoss = 0,
                InitialCapital = 10_000m, FinalEquity = 10_000m, TradingDays = 0,
            },
            EquityCurve = [], RunMode = "Backtest",
        };
        _repository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(record);

        var dto = await _handler.HandleAsync(new GetBacktestStatusQuery(id));

        Assert.NotNull(dto);
        Assert.Equal(id, dto.Id);
        Assert.NotNull(dto.Result);
        Assert.Equal(id, dto.Result.Id);
    }

    [Fact]
    public async Task HandleAsync_UnknownRun_ReturnsNull()
    {
        var id = Guid.NewGuid();
        _repository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns((BacktestRunRecord?)null);

        var dto = await _handler.HandleAsync(new GetBacktestStatusQuery(id));

        Assert.Null(dto);
    }
}
