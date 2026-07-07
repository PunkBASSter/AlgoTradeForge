using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.State;
using AlgoTradeForge.Storage;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.State;

public sealed class FeedStatusCompleteMonthsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FeedStatusManager _manager;

    public FeedStatusCompleteMonthsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"FeedStatusCompleteMonths_{Guid.NewGuid():N}");
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
    public async Task CompleteMonths_RoundTrips_ThroughStore()
    {
        var status = new FeedStatus
        {
            FeedName = FeedNames.Ticks,
            Interval = "",
            CompleteMonths = ["2024-01", "2024-02"],
        };
        await _manager.Save(_tempDir, FeedNames.Ticks, "", status, Ct);

        var loaded = await _manager.Load(_tempDir, FeedNames.Ticks, "", Ct);

        Assert.NotNull(loaded);
        Assert.Equal(new[] { "2024-01", "2024-02" }, loaded.CompleteMonths);

        var json = await File.ReadAllTextAsync(
            Path.Combine(_tempDir, FeedNames.Ticks, "status.json"), Ct);
        Assert.Contains("completeMonths", json);
    }

    [Fact]
    public async Task LegacyStatus_WithoutCompleteMonths_LoadsAsEmpty()
    {
        var feedDir = Path.Combine(_tempDir, FeedNames.Ticks);
        Directory.CreateDirectory(feedDir);
        // Older format written before CompleteMonths existed.
        await File.WriteAllTextAsync(Path.Combine(feedDir, "status.json"),
            "{\"feedName\":\"ticks\",\"interval\":\"\",\"recordCount\":5}", Ct);

        var loaded = await _manager.Load(_tempDir, FeedNames.Ticks, "", Ct);

        Assert.NotNull(loaded);
        Assert.Empty(loaded.CompleteMonths);
    }
}
