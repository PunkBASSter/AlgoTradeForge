using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Live;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Live;

public class GetLiveSessionDataQueryHandlerTests
{
    private static readonly Asset TestAsset = Asset.Crypto("BTCUSDT", "Binance", 2);

    private static readonly DataSubscription TestSubscription = new(TestAsset, TimeSpan.FromMinutes(1));

    private readonly ILiveSessionStore _store = new InMemoryLiveSessionStore();
    private readonly ILiveSessionDataProvider _dataProvider = Substitute.For<ILiveSessionDataProvider>();
    private readonly IHistoryRepository _historyRepo = Substitute.For<IHistoryRepository>();

    private GetLiveSessionDataQueryHandler CreateHandler() =>
        new(_store, _dataProvider, _historyRepo, NullLogger<GetLiveSessionDataQueryHandler>.Instance);

    private static SessionDetails MakeDetails(ILiveConnector? connector = null) =>
        new("paper", connector ?? Substitute.For<ILiveConnector>(),
            "TestStrategy", "1.0", "Binance", "BTCUSDT",
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow);

    private static LiveSessionSnapshot MakeSnapshot(
        IReadOnlyList<Int64Bar>? bars = null,
        IReadOnlyList<Fill>? fills = null,
        IReadOnlyList<Order>? pendingOrders = null,
        IReadOnlyDictionary<string, Position>? positions = null,
        long cash = 10_000_000,
        long initialCash = 10_000_000,
        IReadOnlyList<SubscriptionLastBar>? lastBars = null)
    {
        return new LiveSessionSnapshot(
            bars ?? [],
            fills ?? [],
            pendingOrders ?? [],
            positions ?? new Dictionary<string, Position>(),
            cash,
            initialCash,
            TestAsset,
            [TestSubscription],
            lastBars ?? []);
    }

    [Fact]
    public async Task SessionNotFound_ReturnsNull()
    {
        var handler = CreateHandler();

        var result = await handler.HandleAsync(new GetLiveSessionDataQuery(Guid.NewGuid()));

        Assert.Null(result);
    }

    [Fact]
    public async Task SnapshotNotFound_ReturnsNull()
    {
        var handler = CreateHandler();
        var sessionId = Guid.NewGuid();
        _store.TryAdd(sessionId, MakeDetails());
        _dataProvider.GetSnapshot(sessionId).Returns((LiveSessionSnapshot?)null);

        var result = await handler.HandleAsync(new GetLiveSessionDataQuery(sessionId));

        Assert.Null(result);
    }

    [Fact]
    public async Task ScalesInt64PricesToDecimal()
    {
        var handler = CreateHandler();
        var sessionId = Guid.NewGuid();
        _store.TryAdd(sessionId, MakeDetails());

        // Bar with Int64 values: 6_500_000 * 0.01 = 65,000.00
        var bar = new Int64Bar(1000, 6_500_000, 6_600_000, 6_400_000, 6_550_000, 100);
        _dataProvider.GetSnapshot(sessionId).Returns(MakeSnapshot(bars: [bar]));
        _historyRepo.Load(Arg.Any<DataSubscription>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(new TimeSeries<Int64Bar>());

        var result = await handler.HandleAsync(new GetLiveSessionDataQuery(sessionId));

        Assert.NotNull(result);
        Assert.Single(result.Candles);
        var candle = result.Candles[0];
        Assert.Equal(65_000.00m, candle.Open);
        Assert.Equal(66_000.00m, candle.High);
        Assert.Equal(64_000.00m, candle.Low);
        Assert.Equal(65_500.00m, candle.Close);
        Assert.Equal(100, candle.Volume);
    }

    [Fact]
    public async Task DeduplicatesBarsWithSameTimestamp()
    {
        var handler = CreateHandler();
        var sessionId = Guid.NewGuid();
        _store.TryAdd(sessionId, MakeDetails());

        // Historical and session bar share same timestamp — should deduplicate
        var historicalBar = new Int64Bar(1000, 100, 200, 50, 150, 10);
        var sessionBar = new Int64Bar(1000, 110, 210, 60, 160, 20); // same timestamp
        var sessionBar2 = new Int64Bar(2000, 120, 220, 70, 170, 30); // different timestamp

        var historicalSeries = new TimeSeries<Int64Bar>();
        historicalSeries.Add(historicalBar);

        _dataProvider.GetSnapshot(sessionId).Returns(MakeSnapshot(bars: [sessionBar, sessionBar2]));
        _historyRepo.Load(Arg.Any<DataSubscription>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(historicalSeries);

        var result = await handler.HandleAsync(new GetLiveSessionDataQuery(sessionId));

        Assert.NotNull(result);
        // Should have 2 candles: historical wins for ts=1000, session bar for ts=2000
        Assert.Equal(2, result.Candles.Count);
        Assert.Equal(1000, result.Candles[0].Time);
        Assert.Equal(2000, result.Candles[1].Time);
        // The first candle should have the historical bar's values (first-write-wins dedup)
        Assert.Equal(100 * TestAsset.TickSize, result.Candles[0].Open);
    }

    [Fact]
    public async Task ConvertsFillsCorrectly()
    {
        var handler = CreateHandler();
        var sessionId = Guid.NewGuid();
        _store.TryAdd(sessionId, MakeDetails());

        var fill = new Fill(42, TestAsset, DateTimeOffset.Parse("2026-03-06T12:00:00Z"),
            6_500_000, 0.5m, OrderSide.Buy, 650);

        _dataProvider.GetSnapshot(sessionId).Returns(MakeSnapshot(fills: [fill]));
        _historyRepo.Load(Arg.Any<DataSubscription>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(new TimeSeries<Int64Bar>());

        var result = await handler.HandleAsync(new GetLiveSessionDataQuery(sessionId));

        Assert.NotNull(result);
        Assert.Single(result.Fills);
        var dto = result.Fills[0];
        Assert.Equal(42, dto.OrderId);
        Assert.Equal(65_000.00m, dto.Price);    // 6_500_000 * 0.01
        Assert.Equal(0.5m, dto.Quantity);
        Assert.Equal("Buy", dto.Side);
        Assert.Equal(6.50m, dto.Commission);     // 650 * 0.01
    }

    [Fact]
    public async Task ConvertsPendingOrdersCorrectly()
    {
        var handler = CreateHandler();
        var sessionId = Guid.NewGuid();
        _store.TryAdd(sessionId, MakeDetails());

        var order = new Order
        {
            Id = 7,
            Asset = TestAsset,
            Side = OrderSide.Buy,
            Type = OrderType.Limit,
            Quantity = 0.1m,
            LimitPrice = 6_300_000,
            StopPrice = null,
        };

        _dataProvider.GetSnapshot(sessionId).Returns(MakeSnapshot(pendingOrders: [order]));
        _historyRepo.Load(Arg.Any<DataSubscription>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(new TimeSeries<Int64Bar>());

        var result = await handler.HandleAsync(new GetLiveSessionDataQuery(sessionId));

        Assert.NotNull(result);
        Assert.Single(result.PendingOrders);
        var dto = result.PendingOrders[0];
        Assert.Equal(63_000.00m, dto.LimitPrice); // 6_300_000 * 0.01
        Assert.Null(dto.StopPrice);
        Assert.Equal("Limit", dto.Type);
    }

    [Fact]
    public async Task FiltersZeroQuantityPositions()
    {
        var handler = CreateHandler();
        var sessionId = Guid.NewGuid();
        _store.TryAdd(sessionId, MakeDetails());

        var positions = new Dictionary<string, Position>
        {
            ["BTCUSDT"] = new(TestAsset, 0.5m, 6_500_000, 1000),
            ["ETHUSDT"] = new(TestAsset, 0m, 0, 500), // closed position — should be filtered
        };

        _dataProvider.GetSnapshot(sessionId).Returns(MakeSnapshot(positions: positions));
        _historyRepo.Load(Arg.Any<DataSubscription>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(new TimeSeries<Int64Bar>());

        var result = await handler.HandleAsync(new GetLiveSessionDataQuery(sessionId));

        Assert.NotNull(result);
        Assert.Single(result.Account.Positions);
        Assert.Equal(0.5m, result.Account.Positions[0].Quantity);
    }

    [Fact]
    public async Task ScalesAccountCashValues()
    {
        var handler = CreateHandler();
        var sessionId = Guid.NewGuid();
        _store.TryAdd(sessionId, MakeDetails());

        _dataProvider.GetSnapshot(sessionId).Returns(
            MakeSnapshot(cash: 9_500_000, initialCash: 10_000_000));
        _historyRepo.Load(Arg.Any<DataSubscription>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(new TimeSeries<Int64Bar>());

        var result = await handler.HandleAsync(new GetLiveSessionDataQuery(sessionId));

        Assert.NotNull(result);
        Assert.Equal(100_000.00m, result.Account.InitialCash); // 10_000_000 * 0.01
        Assert.Equal(95_000.00m, result.Account.Cash);         // 9_500_000 * 0.01
    }

    [Fact]
    public async Task BuildsLastBarsFromSnapshotSubscriptions()
    {
        var handler = CreateHandler();
        var sessionId = Guid.NewGuid();
        _store.TryAdd(sessionId, MakeDetails());

        var bar = new Int64Bar(60_000, 6_500_000, 6_600_000, 6_400_000, 6_550_000, 42);
        var lastBars = new List<SubscriptionLastBar>
        {
            new(TestSubscription, bar),
        };

        _dataProvider.GetSnapshot(sessionId).Returns(MakeSnapshot(lastBars: lastBars));
        _historyRepo.Load(Arg.Any<DataSubscription>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(new TimeSeries<Int64Bar>());

        var result = await handler.HandleAsync(new GetLiveSessionDataQuery(sessionId));

        Assert.NotNull(result);
        Assert.Single(result.LastBars);
        var dto = result.LastBars[0];
        Assert.Equal("BTCUSDT", dto.Symbol);
        Assert.Equal("00:01:00", dto.TimeFrame);
        Assert.Equal(65_000.00m, dto.Open);
        Assert.Equal(66_000.00m, dto.High);
        Assert.Equal(64_000.00m, dto.Low);
        Assert.Equal(65_500.00m, dto.Close);
        Assert.Equal(42, dto.Volume);
    }

    [Fact]
    public async Task GracefullyHandlesHistoryLoadFailure()
    {
        var handler = CreateHandler();
        var sessionId = Guid.NewGuid();
        _store.TryAdd(sessionId, MakeDetails());

        var sessionBar = new Int64Bar(1000, 100, 200, 50, 150, 10);
        _dataProvider.GetSnapshot(sessionId).Returns(MakeSnapshot(bars: [sessionBar]));
        _historyRepo.Load(Arg.Any<DataSubscription>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(_ => throw new FileNotFoundException("No CSV data"));

        var result = await handler.HandleAsync(new GetLiveSessionDataQuery(sessionId));

        Assert.NotNull(result);
        Assert.Single(result.Candles); // session bar still present despite history failure
    }

    [Fact]
    public async Task TimeFrameDefaultsWhenNoSubscriptions()
    {
        var handler = CreateHandler();
        var sessionId = Guid.NewGuid();
        _store.TryAdd(sessionId, MakeDetails());

        // Snapshot with no subscriptions
        var snapshot = new LiveSessionSnapshot(
            [], [], [], new Dictionary<string, Position>(),
            10_000_000, 10_000_000, TestAsset, [], []);
        _dataProvider.GetSnapshot(sessionId).Returns(snapshot);

        var result = await handler.HandleAsync(new GetLiveSessionDataQuery(sessionId));

        Assert.NotNull(result);
        Assert.Equal("00:01:00", result.TimeFrame);
    }
}
