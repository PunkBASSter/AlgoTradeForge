using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.State;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.State;

public sealed class FeedStatusManagerTests : IDisposable
{
    private readonly string _tempDir;

    public FeedStatusManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"FeedStatusManagerTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private readonly FeedStatusManager _manager = new();

    [Fact]
    public void Load_NoFile_ReturnsNull()
    {
        var result = _manager.Load(_tempDir, "candles", "1m");

        Assert.Null(result);
    }

    [Fact]
    public void Save_CreatesStatusJson()
    {
        var status = new FeedStatus
        {
            FeedName = "candles",
            Interval = "1m",
            RecordCount = 42,
            Health = CollectionHealth.Healthy
        };

        _manager.Save(_tempDir, "candles", "1m", status);

        var expectedPath = Path.Combine(_tempDir, "candles", "status_1m.json");
        Assert.True(File.Exists(expectedPath));

        var contents = File.ReadAllText(expectedPath);
        Assert.Contains("candles", contents);
        Assert.Contains("42", contents);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var now = DateTimeOffset.UtcNow;
        var status = new FeedStatus
        {
            FeedName = "candles",
            Interval = "5m",
            FirstTimestamp = 1_000_000L,
            LastTimestamp = 2_000_000L,
            LastRunUtc = now,
            RecordCount = 288,
            Gaps = [],
            Health = CollectionHealth.Degraded
        };

        _manager.Save(_tempDir, "candles", "5m", status);
        var loaded = _manager.Load(_tempDir, "candles", "5m");

        Assert.NotNull(loaded);
        Assert.Equal(status.FeedName, loaded.FeedName);
        Assert.Equal(status.Interval, loaded.Interval);
        Assert.Equal(status.FirstTimestamp, loaded.FirstTimestamp);
        Assert.Equal(status.LastTimestamp, loaded.LastTimestamp);
        Assert.Equal(status.RecordCount, loaded.RecordCount);
        Assert.Equal(status.Health, loaded.Health);
        Assert.Empty(loaded.Gaps);
        // DateTimeOffset round-trip — compare to millisecond precision
        Assert.Equal(
            status.LastRunUtc!.Value.ToUnixTimeMilliseconds(),
            loaded.LastRunUtc!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Save_AtomicWrite_NoTmpFileRemains()
    {
        var status = new FeedStatus { FeedName = "candles", Interval = "1h" };

        _manager.Save(_tempDir, "candles", "1h", status);

        var feedDir = Path.Combine(_tempDir, "candles");
        var tmpFiles = Directory.GetFiles(feedDir, "*.tmp");
        Assert.Empty(tmpFiles);
    }

    [Fact]
    public void Save_WithGaps_SerializesCorrectly()
    {
        var status = new FeedStatus
        {
            FeedName = "candles",
            Interval = "1d",
            Gaps =
            [
                new DataGap { FromMs = 100_000L, ToMs = 200_000L },
                new DataGap { FromMs = 300_000L, ToMs = 400_000L }
            ],
            Health = CollectionHealth.Error
        };

        _manager.Save(_tempDir, "candles", "1d", status);

        var json = File.ReadAllText(Path.Combine(_tempDir, "candles", "status_1d.json"));
        Assert.Contains("100000", json);
        Assert.Contains("200000", json);
        Assert.Contains("300000", json);
        Assert.Contains("400000", json);
        Assert.Contains("fromMs", json);
        Assert.Contains("toMs", json);
    }

    [Fact]
    public void Save_DifferentIntervals_SeparateStatusFiles()
    {
        var status1m = new FeedStatus
        {
            FeedName = "candles",
            Interval = "1m",
            RecordCount = 100,
            Health = CollectionHealth.Healthy
        };
        var status1d = new FeedStatus
        {
            FeedName = "candles",
            Interval = "1d",
            RecordCount = 50,
            Health = CollectionHealth.Degraded
        };

        _manager.Save(_tempDir, "candles", "1m", status1m);
        _manager.Save(_tempDir, "candles", "1d", status1d);

        var loaded1m = _manager.Load(_tempDir, "candles", "1m");
        var loaded1d = _manager.Load(_tempDir, "candles", "1d");

        Assert.NotNull(loaded1m);
        Assert.NotNull(loaded1d);
        Assert.Equal(100, loaded1m.RecordCount);
        Assert.Equal(50, loaded1d.RecordCount);
        Assert.Equal(CollectionHealth.Healthy, loaded1m.Health);
        Assert.Equal(CollectionHealth.Degraded, loaded1d.Health);
    }

    [Fact]
    public void Save_EmptyInterval_UsesPlainStatusJson()
    {
        var status = new FeedStatus
        {
            FeedName = "funding-rate",
            Interval = "",
            RecordCount = 10,
        };

        _manager.Save(_tempDir, "funding-rate", "", status);

        var expectedPath = Path.Combine(_tempDir, "funding-rate", "status.json");
        Assert.True(File.Exists(expectedPath));

        var loaded = _manager.Load(_tempDir, "funding-rate", "");
        Assert.NotNull(loaded);
        Assert.Equal(10, loaded.RecordCount);
    }
}
