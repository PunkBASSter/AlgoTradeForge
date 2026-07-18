using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.State;
using AlgoTradeForge.Storage;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.State;

public sealed class FeedStatusManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FeedStatusManager _manager;

    public FeedStatusManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"FeedStatusManagerTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _manager = new FeedStatusManager(new LocalFileStorage());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Load_NoFile_ReturnsNull()
    {
        var result = await _manager.Load(_tempDir, "candles", "1m", Ct);

        Assert.Null(result);
    }

    [Fact]
    public async Task Save_CreatesStatusJson()
    {
        var status = new FeedStatus
        {
            FeedName = "candles",
            Interval = "1m",
            RecordCount = 42,
            Health = CollectionHealth.Healthy
        };

        await _manager.Save(_tempDir, "candles", "1m", status, Ct);

        var expectedPath = Path.Combine(_tempDir, "candles", "status_1m.json");
        Assert.True(File.Exists(expectedPath));

        var contents = File.ReadAllText(expectedPath);
        Assert.Contains("candles", contents);
        Assert.Contains("42", contents);
    }

    [Fact]
    public async Task Save_ThenLoad_RoundTrips()
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

        await _manager.Save(_tempDir, "candles", "5m", status, Ct);
        var loaded = await _manager.Load(_tempDir, "candles", "5m", Ct);

        Assert.NotNull(loaded);
        Assert.Equal(status.FeedName, loaded.FeedName);
        Assert.Equal(status.Interval, loaded.Interval);
        Assert.Equal(status.FirstTimestamp, loaded.FirstTimestamp);
        Assert.Equal(status.LastTimestamp, loaded.LastTimestamp);
        Assert.Equal(status.RecordCount, loaded.RecordCount);
        Assert.Equal(status.Health, loaded.Health);
        Assert.Empty(loaded.Gaps);
        Assert.Equal(
            status.LastRunUtc!.Value.ToUnixTimeMilliseconds(),
            loaded.LastRunUtc!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Save_AtomicWrite_NoTmpFileRemains()
    {
        var status = new FeedStatus { FeedName = "candles", Interval = "1h" };

        await _manager.Save(_tempDir, "candles", "1h", status, Ct);

        var feedDir = Path.Combine(_tempDir, "candles");
        var tmpFiles = Directory.GetFiles(feedDir, "*.tmp");
        Assert.Empty(tmpFiles);
    }

    [Fact]
    public async Task Save_WithGaps_SerializesCorrectly()
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

        await _manager.Save(_tempDir, "candles", "1d", status, Ct);

        var json = File.ReadAllText(Path.Combine(_tempDir, "candles", "status_1d.json"));
        Assert.Contains("100000", json);
        Assert.Contains("200000", json);
        Assert.Contains("300000", json);
        Assert.Contains("400000", json);
        Assert.Contains("fromMs", json);
        Assert.Contains("toMs", json);
    }

    [Fact]
    public async Task Save_DifferentIntervals_SeparateStatusFiles()
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

        await _manager.Save(_tempDir, "candles", "1m", status1m, Ct);
        await _manager.Save(_tempDir, "candles", "1d", status1d, Ct);

        var loaded1m = await _manager.Load(_tempDir, "candles", "1m", Ct);
        var loaded1d = await _manager.Load(_tempDir, "candles", "1d", Ct);

        Assert.NotNull(loaded1m);
        Assert.NotNull(loaded1d);
        Assert.Equal(100, loaded1m.RecordCount);
        Assert.Equal(50, loaded1d.RecordCount);
        Assert.Equal(CollectionHealth.Healthy, loaded1m.Health);
        Assert.Equal(CollectionHealth.Degraded, loaded1d.Health);
    }

    [Fact]
    public async Task Save_EmptyInterval_UsesPlainStatusJson()
    {
        var status = new FeedStatus
        {
            FeedName = "funding-rate",
            Interval = "",
            RecordCount = 10,
        };

        await _manager.Save(_tempDir, "funding-rate", "", status, Ct);

        var expectedPath = Path.Combine(_tempDir, "funding-rate", "status.json");
        Assert.True(File.Exists(expectedPath));

        var loaded = await _manager.Load(_tempDir, "funding-rate", "", Ct);
        Assert.NotNull(loaded);
        Assert.Equal(10, loaded.RecordCount);
    }

    [Fact]
    public async Task Update_NoExistingStatus_PassesNullToMutate_AndPersists()
    {
        FeedStatus? seen = new FeedStatus();
        await _manager.Update(_tempDir, FeedNames.OpenInterest, "5m",
            existing =>
            {
                seen = existing;
                return new FeedStatus
                {
                    FeedName = FeedNames.OpenInterest,
                    Interval = "5m",
                    RecordCount = 7,
                };
            },
            Ct);

        Assert.Null(seen);
        var loaded = await _manager.Load(_tempDir, FeedNames.OpenInterest, "5m", Ct);
        Assert.NotNull(loaded);
        Assert.Equal(7, loaded.RecordCount);
    }

    [Fact]
    public async Task Update_ConcurrentIncrements_NoLostUpdate()
    {
        const int n = 50;
        await _manager.Update(_tempDir, FeedNames.OpenInterest, "5m",
            _ => new FeedStatus { FeedName = FeedNames.OpenInterest, Interval = "5m", RecordCount = 0 },
            Ct);

        await Task.WhenAll(Enumerable.Range(0, n).Select(_ =>
            _manager.Update(_tempDir, FeedNames.OpenInterest, "5m",
                existing => new FeedStatus
                {
                    FeedName = existing!.FeedName,
                    Interval = existing.Interval,
                    FirstTimestamp = existing.FirstTimestamp,
                    LastTimestamp = existing.LastTimestamp,
                    RecordCount = existing.RecordCount + 1,
                    Gaps = existing.Gaps,
                    Health = existing.Health,
                    CompleteMonths = existing.CompleteMonths,
                },
                Ct)));

        var final = await _manager.Load(_tempDir, FeedNames.OpenInterest, "5m", Ct);
        Assert.Equal(n, final!.RecordCount);
    }
}
