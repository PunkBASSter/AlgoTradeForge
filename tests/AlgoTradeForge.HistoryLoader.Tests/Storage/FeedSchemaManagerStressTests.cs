using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using AlgoTradeForge.Storage;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Storage;

/// <summary>
/// P1a-9, P1a-10 — concurrency upgrade verification:
/// <list type="bullet">
///   <item><see cref="FeedSchemaManager.ManifestChanged"/> fires after a successful write.</item>
///   <item>Two parallel writers on distinct feed-ids of the same asset BOTH end up in the
///         final manifest, with no entry overwritten and no <c>*.tmp</c> left behind.
///         Repeated 100× to flush out lock-ordering races; each iteration aligns the two
///         finalizers via <see cref="ManualResetEventSlim"/>.</item>
/// </list>
/// </summary>
public sealed class FeedSchemaManagerStressTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"FeedSchemaManagerStressTests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string AssetDir(string name)
    {
        var path = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ManifestChanged_FiresAfterEnsureSchemaWithAssetDirAbsolutePath()
    {
        var manager = new FeedSchemaManager(new LocalFileStorage());
        var assetDir = AssetDir("BTCUSDT_Event");
        var raised = new List<string>();
        manager.ManifestChanged += raised.Add;

        await manager.EnsureSchema(assetDir, "funding-rate", "8h", ["rate", "mark"], ct: Ct);

        Assert.Single(raised);
        Assert.Equal(Path.GetFullPath(assetDir), raised[0]);
    }

    [Fact]
    public async Task ManifestChanged_FiresOncePerWrite()
    {
        var manager = new FeedSchemaManager(new LocalFileStorage());
        var assetDir = AssetDir("BTCUSDT_EventCount");
        var count = 0;
        manager.ManifestChanged += _ => Interlocked.Increment(ref count);

        await manager.EnsureSchema(assetDir, "feed-a", "1m", ["x"], ct: Ct);
        await manager.EnsureSchema(assetDir, "feed-b", "5m", ["y"], ct: Ct);
        await manager.EnsureCandleConfig(assetDir, decimalDigits: 2, "1m", Ct);

        Assert.Equal(3, count);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task ConcurrentWriters_DistinctFeedIds_AllEntriesPersist()
    {
        const int iterations = 100;
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < iterations; i++)
        {
            var assetDir = AssetDir($"BTCUSDT_Stress_{i:D3}");
            var manager = new FeedSchemaManager(new LocalFileStorage());

            // The barrier holds both threads at ready-to-finalize so they hit the
            // exclusive lock within ~microseconds of each other every iteration —
            // not just the first. Without this, only the first iteration races.
            using var barrier = new ManualResetEventSlim(initialState: false);

            var t1 = Task.Run(async () =>
            {
                barrier.Wait(ct);
                await manager.EnsureSchema(assetDir, "feed-A", "1m", ["x"], ct: ct);
            }, ct);
            var t2 = Task.Run(async () =>
            {
                barrier.Wait(ct);
                await manager.EnsureSchema(assetDir, "feed-B", "5m", ["y"], ct: ct);
            }, ct);

            // Brief delay so both Tasks reach barrier.Wait before we release.
            await Task.Delay(1, ct);
            barrier.Set();

            await Task.WhenAll(t1, t2);

            // Both entries must be present.
            var loaded = await manager.Load(assetDir, ct);
            Assert.NotNull(loaded);
            Assert.True(loaded!.Feeds.ContainsKey("feed-A"),
                $"Iteration {i}: feed-A missing — writer race lost an entry.");
            Assert.True(loaded.Feeds.ContainsKey("feed-B"),
                $"Iteration {i}: feed-B missing — writer race lost an entry.");

            // No leftover *.tmp file.
            var tmpPath = Path.Combine(assetDir, "feeds.json.tmp");
            Assert.False(File.Exists(tmpPath),
                $"Iteration {i}: *.tmp left behind from interrupted rename.");
        }
    }
}
