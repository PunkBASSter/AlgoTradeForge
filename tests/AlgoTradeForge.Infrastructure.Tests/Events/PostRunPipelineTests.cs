using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Infrastructure.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.Events;

public class PostRunPipelineTests
{
    private readonly IEventIndexBuilder _indexBuilder = Substitute.For<IEventIndexBuilder>();
    private readonly ITradeDbWriter _tradeDbWriter = Substitute.For<ITradeDbWriter>();
    private readonly ILogger<PostRunPipeline> _logger = Substitute.For<ILogger<PostRunPipeline>>();

    private static readonly RunIdentity Identity = new()
    {
        StrategyName = "TestStrat",
        AssetName = "AAPL",
        StartTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        EndTime = new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero),
        InitialCash = 100_000L,
        RunMode = ExportMode.Backtest,
        RunTimestamp = DateTimeOffset.UtcNow,
    };

    private static readonly RunSummary Summary = new(1000, 105_000L, 42, TimeSpan.FromSeconds(3));

    private PostRunPipeline CreatePipeline(bool buildIndex = true) =>
        new(_indexBuilder, _tradeDbWriter,
            Options.Create(new PostRunPipelineOptions { BuildDebugIndex = buildIndex }),
            _logger);

    [Fact]
    public void Execute_BuildsIndexAndTrades_WhenEnabled()
    {
        var pipeline = CreatePipeline(buildIndex: true);

        var result = pipeline.Execute("/tmp/run", Identity, Summary);

        Assert.True(result.IndexBuilt);
        Assert.True(result.TradesInserted);
        Assert.Null(result.IndexError);
        Assert.Null(result.TradesError);

        _indexBuilder.Received(1).Build("/tmp/run");
        _tradeDbWriter.Received(1).WriteFromJsonl("/tmp/run", Identity, Summary);
    }

    [Fact]
    public void Execute_SkipsIndex_WhenDisabled()
    {
        var pipeline = CreatePipeline(buildIndex: false);

        var result = pipeline.Execute("/tmp/run", Identity, Summary);

        Assert.False(result.IndexBuilt);
        Assert.True(result.TradesInserted);
        Assert.Null(result.IndexError);

        _indexBuilder.DidNotReceive().Build(Arg.Any<string>());
        _tradeDbWriter.Received(1).WriteFromJsonl("/tmp/run", Identity, Summary);
    }

    [Fact]
    public void Execute_IndexError_StillWritesTrades()
    {
        _indexBuilder.When(x => x.Build(Arg.Any<string>())).Throw(new IOException("disk full"));
        var pipeline = CreatePipeline(buildIndex: true);

        var result = pipeline.Execute("/tmp/run", Identity, Summary);

        Assert.False(result.IndexBuilt);
        Assert.True(result.TradesInserted);
        Assert.Equal("disk full", result.IndexError);
        Assert.Null(result.TradesError);

        _tradeDbWriter.Received(1).WriteFromJsonl("/tmp/run", Identity, Summary);
    }

    [Fact]
    public void Execute_TradesError_StillReportsResult()
    {
        _tradeDbWriter.When(x => x.WriteFromJsonl(Arg.Any<string>(), Arg.Any<RunIdentity>(), Arg.Any<RunSummary>()))
            .Throw(new InvalidOperationException("db locked"));
        var pipeline = CreatePipeline(buildIndex: true);

        var result = pipeline.Execute("/tmp/run", Identity, Summary);

        Assert.True(result.IndexBuilt);
        Assert.False(result.TradesInserted);
        Assert.Null(result.IndexError);
        Assert.Equal("db locked", result.TradesError);
    }

    [Fact]
    public void Execute_BothFail_NeitherThrows()
    {
        _indexBuilder.When(x => x.Build(Arg.Any<string>())).Throw(new IOException("index fail"));
        _tradeDbWriter.When(x => x.WriteFromJsonl(Arg.Any<string>(), Arg.Any<RunIdentity>(), Arg.Any<RunSummary>()))
            .Throw(new InvalidOperationException("trades fail"));
        var pipeline = CreatePipeline(buildIndex: true);

        var result = pipeline.Execute("/tmp/run", Identity, Summary);

        Assert.False(result.IndexBuilt);
        Assert.False(result.TradesInserted);
        Assert.Equal("index fail", result.IndexError);
        Assert.Equal("trades fail", result.TradesError);
    }
}
