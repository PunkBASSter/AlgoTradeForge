using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using AlgoTradeForge.Storage;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Storage;

public sealed class FeedSchemaManagerOptimisticConcurrencyTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"FeedSchemaManagerOcc_{Guid.NewGuid():N}");

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
    public async Task PersistentConflict_ExhaustsMaxAttempts_ThenThrows()
    {
        var fs = Substitute.For<IFileStorage>();
        fs.ReadWithEtag(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((StoredObject?)null);
        fs.WriteIfMatch(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Throws(_ => new ConcurrencyConflictException("path", null, "someone-else-wrote"));

        var sut = new FeedSchemaManager(fs);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            sut.EnsureSchema(AssetDir("BTCUSDT_PersistConflict"), "feed-x", "1m", ["ts"], ct: Ct));

        await fs.Received(5).WriteIfMatch(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancellationBetweenAttempts_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var fs = Substitute.For<IFileStorage>();
        fs.ReadWithEtag(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((StoredObject?)null);
        fs.WriteIfMatch(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Throws(_ =>
            {
                cts.Cancel();
                return new ConcurrencyConflictException("path", null, "actual");
            });

        var sut = new FeedSchemaManager(fs);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.EnsureSchema(AssetDir("BTCUSDT_CancelInLoop"), "feed-x", "1m", ["ts"], ct: cts.Token));
    }

    [Fact]
    public async Task ConflictThenSuccess_Retries_AndEventFiresOnce()
    {
        var fs = Substitute.For<IFileStorage>();

        // Models a real retry scenario: first read sees an empty key (we plan to create),
        // we try to create, a concurrent writer wins the race, our retry re-reads the
        // concurrent writer's content with its ETag, we conditionally overwrite, success.
        fs.ReadWithEtag(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                (StoredObject?)null,
                new StoredObject("{}", "etag-after-conflict"));

        var calls = 0;
        fs.WriteIfMatch(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                if (calls == 1) throw new ConcurrencyConflictException("path", null, "etag-after-conflict");
                return Task.FromResult("new-etag");
            });

        var sut = new FeedSchemaManager(fs);
        var events = new List<string>();
        sut.ManifestChanged += events.Add;

        await sut.EnsureSchema(AssetDir("BTCUSDT_Retry"), "feed-x", "1m", ["ts"], ct: Ct);

        Assert.Equal(2, calls);
        Assert.Single(events);
        await fs.Received(2).ReadWithEtag(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
