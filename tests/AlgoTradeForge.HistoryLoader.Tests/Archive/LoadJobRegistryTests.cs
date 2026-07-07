using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Archive.Jobs;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class LoadJobRegistryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    private const string AssetDir = "/data/binance/BTCUSDT";
    private const string FeedKey = "/data/binance/BTCUSDT|candles|1h";

    private static LoadJob NewJob(string jobId, string feedName = "candles", string interval = "1h") =>
        new(JobId: jobId, Exchange: "binance", Symbol: "BTCUSDT", AssetType: "spot",
            FeedName: feedName, Interval: interval,
            From: new DateOnly(2024, 1, 1), To: new DateOnly(2024, 12, 31));

    private static (LoadJobRegistry registry, TestClock clock) BuildSubjects(
        int maxQueueDepth = 16, int retentionMinutes = 30)
    {
        var clock = new TestClock(T0);
        var options = Options.Create(new HistoryLoaderOptions
        {
            Load = new LoadOptions
            {
                MaxQueueDepth = maxQueueDepth,
                JobRetentionMinutes = retentionMinutes,
            }
        });
        return (new LoadJobRegistry(options, clock), clock);
    }

    [Fact]
    public void TryEnqueue_Accepts_AndGetReturnsSnapshot()
    {
        var (reg, _) = BuildSubjects();
        var outcome = reg.TryEnqueue(NewJob("j1"), FeedKey);

        var accepted = Assert.IsType<LoadEnqueueOutcome.Accepted>(outcome);
        Assert.Equal(LoadJobState.Queued, accepted.Record.State);

        var snap = reg.Get("j1");
        Assert.NotNull(snap);
        Assert.Equal("j1", snap!.JobId);
        Assert.Equal("queued", snap.State);
        Assert.Equal("BTCUSDT", snap.Symbol);
    }

    [Fact]
    public void SecondEnqueue_SameFeedKey_ReturnsFeedBusy_WithActiveJobId()
    {
        var (reg, _) = BuildSubjects();
        reg.TryEnqueue(NewJob("j1"), FeedKey);

        var outcome = reg.TryEnqueue(NewJob("j2"), FeedKey);

        var busy = Assert.IsType<LoadEnqueueOutcome.FeedBusy>(outcome);
        Assert.Equal("j1", busy.ActiveJobId);
    }

    [Fact]
    public void Enqueue_AfterTerminal_Accepts()
    {
        var (reg, _) = BuildSubjects();
        reg.TryEnqueue(NewJob("j1"), FeedKey);
        reg.OnStarted("j1");
        reg.OnCompleted("j1");

        var outcome = reg.TryEnqueue(NewJob("j2"), FeedKey);
        Assert.IsType<LoadEnqueueOutcome.Accepted>(outcome);
    }

    [Fact]
    public void QueueFull_ReturnsQueueFull()
    {
        var (reg, _) = BuildSubjects(maxQueueDepth: 1);
        const string feedKey2 = "/data/binance/BTCUSDT|trades|1m";

        reg.TryEnqueue(NewJob("j1"), FeedKey);
        var outcome = reg.TryEnqueue(NewJob("j2", "trades", "1m"), feedKey2);

        Assert.IsType<LoadEnqueueOutcome.QueueFull>(outcome);
        Assert.Null(reg.Get("j2"));
    }

    [Fact]
    public void Get_EvictsTerminal_PastRetention()
    {
        var (reg, clock) = BuildSubjects(retentionMinutes: 30);
        reg.TryEnqueue(NewJob("j1"), FeedKey);
        reg.OnStarted("j1");
        reg.OnCompleted("j1");

        Assert.NotNull(reg.Get("j1"));

        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Null(reg.Get("j1"));
    }

    [Fact]
    public void ActiveJobForSymbol_ReturnsActiveJobId_ForSameAssetDir()
    {
        var (reg, _) = BuildSubjects();
        reg.TryEnqueue(NewJob("j1"), FeedKey);

        Assert.Equal("j1", reg.ActiveJobForSymbol(AssetDir));

        reg.OnStarted("j1");
        reg.OnCompleted("j1");

        Assert.Null(reg.ActiveJobForSymbol(AssetDir));
    }

    [Fact]
    public void Snapshot_State_IsLowercaseWireString()
    {
        // Pin all four values — the FE union type depends on these exact strings.
        Assert.Equal("queued",   StateString(LoadJobState.Queued));
        Assert.Equal("running",  StateString(LoadJobState.Running));
        Assert.Equal("complete", StateString(LoadJobState.Complete));
        Assert.Equal("error",    StateString(LoadJobState.Error));
    }

    private static string StateString(LoadJobState state)
    {
        var record = new LoadJobRecord
        {
            FeedKey = "pin",
            Job = NewJob("j-pin"),
            QueuedAt = T0,
            State = state,
        };
        return record.Snapshot().State;
    }
}
