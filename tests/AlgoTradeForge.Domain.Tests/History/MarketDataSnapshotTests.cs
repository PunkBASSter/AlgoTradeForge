using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.History;

public class MarketDataSnapshotTests
{
    private static readonly Asset TestAsset = Asset.Crypto("BTCUSDT", "Binance", 2);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static TimeSeries<Int64Bar> MakeSeries(int count = 1)
    {
        var series = new TimeSeries<Int64Bar>(Start, OneMinute);
        for (var i = 0; i < count; i++)
            series.Add(new Int64Bar(100, 200, 50, 150, 1000));
        return series;
    }

    [Fact]
    public void Constructor_EmptyDictionary_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new MarketDataSnapshot(new Dictionary<DataSubscription, TimeSeries<Int64Bar>>()));
    }

    [Fact]
    public void Constructor_NullDictionary_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MarketDataSnapshot(null!));
    }

    [Fact]
    public void Indexer_ValidSubscription_ReturnsSeries()
    {
        var sub = new DataSubscription(TestAsset, OneMinute);
        var series = MakeSeries();
        var snapshot = new MarketDataSnapshot(new Dictionary<DataSubscription, TimeSeries<Int64Bar>>
        {
            [sub] = series
        });

        Assert.Same(series, snapshot[sub]);
    }

    [Fact]
    public void Indexer_MissingSubscription_ThrowsKeyNotFound()
    {
        var sub = new DataSubscription(TestAsset, OneMinute);
        var missingSub = new DataSubscription(TestAsset, TimeSpan.FromHours(1));
        var snapshot = new MarketDataSnapshot(new Dictionary<DataSubscription, TimeSeries<Int64Bar>>
        {
            [sub] = MakeSeries()
        });

        Assert.Throws<KeyNotFoundException>(() => snapshot[missingSub]);
    }

    [Fact]
    public void TryGet_PresentSubscription_ReturnsTrue()
    {
        var sub = new DataSubscription(TestAsset, OneMinute);
        var series = MakeSeries();
        var snapshot = new MarketDataSnapshot(new Dictionary<DataSubscription, TimeSeries<Int64Bar>>
        {
            [sub] = series
        });

        Assert.True(snapshot.TryGet(sub, out var result));
        Assert.Same(series, result);
    }

    [Fact]
    public void TryGet_AbsentSubscription_ReturnsFalse()
    {
        var sub = new DataSubscription(TestAsset, OneMinute);
        var missingSub = new DataSubscription(TestAsset, TimeSpan.FromHours(1));
        var snapshot = new MarketDataSnapshot(new Dictionary<DataSubscription, TimeSeries<Int64Bar>>
        {
            [sub] = MakeSeries()
        });

        Assert.False(snapshot.TryGet(missingSub, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void Subscriptions_ReturnsAllKeys()
    {
        var sub1 = new DataSubscription(TestAsset, OneMinute);
        var sub2 = new DataSubscription(TestAsset, TimeSpan.FromHours(1));
        var snapshot = new MarketDataSnapshot(new Dictionary<DataSubscription, TimeSeries<Int64Bar>>
        {
            [sub1] = MakeSeries(),
            [sub2] = MakeSeries()
        });

        Assert.Equal(2, snapshot.Subscriptions.Count);
        Assert.Contains(sub1, snapshot.Subscriptions);
        Assert.Contains(sub2, snapshot.Subscriptions);
    }
}
