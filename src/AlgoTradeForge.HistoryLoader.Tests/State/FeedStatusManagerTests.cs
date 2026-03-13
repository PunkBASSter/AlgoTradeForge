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
        var result = _manager.Load(_tempDir, "1m");

        Assert.Null(result);
    }

    [Fact]
    public void Save_CreatesStatusJson()
    {
        var status = new FeedStatus
        {
            FeedName = "klines",
            Interval = "1m",
            RecordCount = 42,
            Health = CollectionHealth.Healthy
        };

        _manager.Save(_tempDir, "1m", status);

        var expectedPath = Path.Combine(_tempDir, "1m", "status.json");
        Assert.True(File.Exists(expectedPath));

        var contents = File.ReadAllText(expectedPath);
        Assert.Contains("klines", contents);
        Assert.Contains("42", contents);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var now = DateTimeOffset.UtcNow;
        var status = new FeedStatus
        {
            FeedName = "klines",
            Interval = "5m",
            FirstTimestamp = 1_000_000L,
            LastTimestamp = 2_000_000L,
            LastRunUtc = now,
            RecordCount = 288,
            Gaps = [],
            Health = CollectionHealth.Degraded
        };

        _manager.Save(_tempDir, "5m", status);
        var loaded = _manager.Load(_tempDir, "5m");

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
        var status = new FeedStatus { FeedName = "klines", Interval = "1h" };

        _manager.Save(_tempDir, "1h", status);

        var feedDir = Path.Combine(_tempDir, "1h");
        var tmpFiles = Directory.GetFiles(feedDir, "*.tmp");
        Assert.Empty(tmpFiles);
    }

    [Fact]
    public void Save_WithGaps_SerializesCorrectly()
    {
        var status = new FeedStatus
        {
            FeedName = "klines",
            Interval = "1d",
            Gaps =
            [
                new DataGap { FromMs = 100_000L, ToMs = 200_000L },
                new DataGap { FromMs = 300_000L, ToMs = 400_000L }
            ],
            Health = CollectionHealth.Error
        };

        _manager.Save(_tempDir, "1d", status);

        var json = File.ReadAllText(Path.Combine(_tempDir, "1d", "status.json"));
        Assert.Contains("100000", json);
        Assert.Contains("200000", json);
        Assert.Contains("300000", json);
        Assert.Contains("400000", json);
        Assert.Contains("fromMs", json);
        Assert.Contains("toMs", json);
    }
}
