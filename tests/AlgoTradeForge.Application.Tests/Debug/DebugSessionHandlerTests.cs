using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Debug;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Application.Tests.TestUtilities;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Debug;

public class DebugSessionHandlerTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    private readonly IAssetRepository _assetRepo = Substitute.For<IAssetRepository>();
    private readonly IStrategyFactory _strategyFactory = Substitute.For<IStrategyFactory>();
    private readonly IHistoryRepository _historyRepo = Substitute.For<IHistoryRepository>();
    private readonly IMetricsCalculator _metricsCalc = new MetricsCalculator();
    private readonly BacktestEngine _engine = new(new BarMatcher(), new OrderValidator());
    private readonly IDebugSessionStore _sessionStore = new InMemoryDebugSessionStore();
    private readonly IRunSinkFactory _runSinkFactory = Substitute.For<IRunSinkFactory>();
    private readonly IPostRunPipeline _postRunPipeline = Substitute.For<IPostRunPipeline>();

    public DebugSessionHandlerTests()
    {
        var sink = Substitute.For<IRunSink>();
        sink.RunFolderPath.Returns(Path.Combine(Path.GetTempPath(), "test-run"));
        _runSinkFactory.Create(Arg.Any<RunIdentity>()).Returns(sink);
    }

    private BacktestPreparer CreatePreparer() =>
        new(_assetRepo, _strategyFactory, _historyRepo);

    private StartDebugSessionCommandHandler CreateStartHandler() =>
        new(_engine, CreatePreparer(), _metricsCalc, _sessionStore, _runSinkFactory, _postRunPipeline);

    private SendDebugCommandHandler CreateSendHandler() =>
        new(_sessionStore);

    private void SetupAssetAndStrategy(int barCount = 10)
    {
        var asset = TestAssets.Aapl;
        _assetRepo.GetByNameAsync("AAPL", "NASDAQ", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Asset?>(asset));

        var sub = new DataSubscription(asset, OneMinute, IsExportable: true);
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.DataSubscriptions.Returns(new List<DataSubscription> { sub });
        _strategyFactory.Create("TestStrategy", Arg.Any<IIndicatorFactory>(), Arg.Any<IDictionary<string, object>?>())
            .Returns(strategy);

        var bars = TestBars.CreateSeries(Start, OneMinute, barCount);
        _historyRepo.Load(Arg.Any<DataSubscription>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(bars);
    }

    [Fact]
    public async Task StartSession_ReturnsSessionId()
    {
        SetupAssetAndStrategy();

        var dto = await CreateStartHandler().HandleAsync(new StartDebugSessionCommand
        {
            AssetName = "AAPL",
            Exchange = "NASDAQ",
            StrategyName = "TestStrategy",
            InitialCash = 100_000m,
            StartTime = Start,
            EndTime = Start.AddDays(1),
        });

        Assert.NotEqual(Guid.Empty, dto.SessionId);
        Assert.Equal("AAPL", dto.AssetName);
        Assert.Equal("TestStrategy", dto.StrategyName);

        // Session should be in the store
        var session = _sessionStore.Get(dto.SessionId);
        Assert.NotNull(session);

        try
        {
            // Assertions on session state would go here
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartSession_ThenStep_ThenContinue_CompletesSuccessfully()
    {
        SetupAssetAndStrategy(barCount: 5);

        var startHandler = CreateStartHandler();
        var sendHandler = CreateSendHandler();

        var sessionDto = await startHandler.HandleAsync(new StartDebugSessionCommand
        {
            AssetName = "AAPL",
            Exchange = "NASDAQ",
            StrategyName = "TestStrategy",
            InitialCash = 100_000m,
            StartTime = Start,
            EndTime = Start.AddDays(1),
        });

        // Step to first bar
        var step1 = await sendHandler.HandleAsync(new SendDebugCommandRequest
        {
            SessionId = sessionDto.SessionId,
            Command = new DebugCommand.NextBar()
        });

        Assert.True(step1.SessionActive);
        Assert.Equal(1, step1.SequenceNumber);

        // Step to second bar
        var step2 = await sendHandler.HandleAsync(new SendDebugCommandRequest
        {
            SessionId = sessionDto.SessionId,
            Command = new DebugCommand.NextBar()
        });

        Assert.Equal(2, step2.SequenceNumber);

        // Continue to completion
        var stepFinal = await sendHandler.HandleAsync(new SendDebugCommandRequest
        {
            SessionId = sessionDto.SessionId,
            Command = new DebugCommand.Continue()
        });

        // Wait for engine to complete
        var session = _sessionStore.Get(sessionDto.SessionId)!;
        try
        {
            var result = await session.RunTask!;

            Assert.NotNull(result);
            Assert.False(session.Probe.IsRunning);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task SendCommand_ToNonExistentSession_Throws()
    {
        var handler = CreateSendHandler();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.HandleAsync(new SendDebugCommandRequest
            {
                SessionId = Guid.NewGuid(),
                Command = new DebugCommand.NextBar()
            }));
    }

    [Fact]
    public async Task SendCommand_AfterCompletion_ReturnsInactive()
    {
        SetupAssetAndStrategy(barCount: 3);

        var startHandler = CreateStartHandler();
        var sendHandler = CreateSendHandler();

        var sessionDto = await startHandler.HandleAsync(new StartDebugSessionCommand
        {
            AssetName = "AAPL",
            Exchange = "NASDAQ",
            StrategyName = "TestStrategy",
            InitialCash = 100_000m,
            StartTime = Start,
            EndTime = Start.AddDays(1),
        });

        // Continue to completion
        await sendHandler.HandleAsync(new SendDebugCommandRequest
        {
            SessionId = sessionDto.SessionId,
            Command = new DebugCommand.Continue()
        });

        var session = _sessionStore.Get(sessionDto.SessionId)!;
        try
        {
            await session.RunTask!;

            // Send command after completion
            var result = await sendHandler.HandleAsync(new SendDebugCommandRequest
            {
                SessionId = sessionDto.SessionId,
                Command = new DebugCommand.NextBar()
            });

            Assert.False(result.SessionActive);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }
}
