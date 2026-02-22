using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Optimization;

public class GetOptimizationStatusQueryHandlerTests
{
    private readonly RunProgressCache _progressCache;
    private readonly IRunRepository _repository = Substitute.For<IRunRepository>();
    private readonly GetOptimizationStatusQueryHandler _handler;

    public GetOptimizationStatusQueryHandlerTests()
    {
        var distributedCache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        _progressCache = new RunProgressCache(distributedCache);
        _handler = new GetOptimizationStatusQueryHandler(_progressCache, _repository);
    }

    [Fact]
    public async Task HandleAsync_ActiveRun_ReturnsProgressWithNoResult()
    {
        var id = Guid.NewGuid();
        await _progressCache.SetProgressAsync(id, 25, 100);

        var dto = await _handler.HandleAsync(new GetOptimizationStatusQuery(id));

        Assert.NotNull(dto);
        Assert.Equal(id, dto.Id);
        Assert.Equal(25, dto.CompletedCombinations);
        Assert.Equal(100, dto.TotalCombinations);
        Assert.Null(dto.Result);
    }

    [Fact]
    public async Task HandleAsync_CompletedRun_ReturnsWithResult()
    {
        var id = Guid.NewGuid();
        var record = new OptimizationRunRecord
        {
            Id = id, StrategyName = "S", StrategyVersion = "1",
            StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
            DurationMs = 500, TotalCombinations = 50, SortBy = "SharpeRatio",
            DataStart = DateTimeOffset.UtcNow, DataEnd = DateTimeOffset.UtcNow,
            InitialCash = 10_000m, Commission = 0m, SlippageTicks = 0,
            MaxParallelism = 4, AssetName = "BTC", Exchange = "Binance",
            TimeFrame = "00:01:00", Trials = [],
        };
        _repository.GetOptimizationByIdAsync(id, Arg.Any<CancellationToken>()).Returns(record);

        var dto = await _handler.HandleAsync(new GetOptimizationStatusQuery(id));

        Assert.NotNull(dto);
        Assert.Equal(id, dto.Id);
        Assert.Equal(50, dto.CompletedCombinations);
        Assert.Equal(50, dto.TotalCombinations);
        Assert.NotNull(dto.Result);
    }

    [Fact]
    public async Task HandleAsync_UnknownRun_ReturnsNull()
    {
        var id = Guid.NewGuid();
        _repository.GetOptimizationByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns((OptimizationRunRecord?)null);

        var dto = await _handler.HandleAsync(new GetOptimizationStatusQuery(id));

        Assert.Null(dto);
    }
}
