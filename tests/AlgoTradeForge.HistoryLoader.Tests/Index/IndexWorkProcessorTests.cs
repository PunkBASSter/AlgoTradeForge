using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Index;

public sealed class IndexWorkProcessorTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("atf-proc-").FullName;
    private SqliteHistoryIndex _index = null!;
    private readonly ISchemaManager _schema = Substitute.For<ISchemaManager>();
    private readonly IFeedStatusStore _statusStore = Substitute.For<IFeedStatusStore>();
    private IndexWorkProcessor _processor = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var init = new HistoryIndexInitializer(Path.Combine(_root, "idx.sqlite"));
        await init.EnsureCreated(Ct);
        _index = new SqliteHistoryIndex(init, init.ConnectionString + ";Pooling=False");

        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = Path.Combine(_root, "data") });

        _processor = new IndexWorkProcessor(
            _index, new FeedMonthScanner(), _schema, _statusStore,
            Substitute.For<IIndexRebuilder>(), options, NullLogger<IndexWorkProcessor>.Instance);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task FeedTouched_UpsertsStatusAndMonths()
    {
        var assetDir = Path.Combine(_root, "data", "binance", "BTCUSDT");
        var feedDir = Path.Combine(assetDir, "candles");
        Directory.CreateDirectory(feedDir);
        File.WriteAllLines(Path.Combine(feedDir, "2024-01_1h.csv"),
            new[] { "ts,o,h,l,c,v" }.Concat(Enumerable.Range(0, 10).Select(i => $"{i},1,1,1,1,1")));
        _statusStore.Load(assetDir, "candles", "1h", Arg.Any<CancellationToken>())
            .Returns(new FeedStatus { FeedName = "candles", Interval = "1h", FirstTimestamp = 1, LastTimestamp = 2, RecordCount = 10 });

        await _processor.Process(new IndexWork.FeedTouched(assetDir, "candles", "1h"), Ct);

        var status = Assert.Single(await _index.GetFeedStatuses("binance", "BTCUSDT", Ct));
        Assert.Equal(10, status.RecordCount);
        var month = Assert.Single(await _index.GetMonths("binance", "BTCUSDT", "candles", "1h", Ct));
        Assert.Equal(("2024-01", 10L), (month.Month, month.Rows));
    }

    [Fact]
    public async Task ManifestTouched_UpsertsAssetRow_AndRemovesWhenManifestGone()
    {
        var assetDir = Path.Combine(_root, "data", "binance", "BTCUSDT_perp");
        _schema.Load(assetDir, Arg.Any<CancellationToken>())
            .Returns(new FeedMetadata());

        await _processor.Process(new IndexWork.ManifestTouched(assetDir), Ct);
        var row = await _index.GetAsset("binance", "BTCUSDT_perp", Ct);
        Assert.NotNull(row);
        Assert.Equal("BTCUSDT", row!.Symbol);   // AssetDirectoryClassifier strips _perp

        _schema.Load(assetDir, Arg.Any<CancellationToken>()).Returns((FeedMetadata?)null);
        await _processor.Process(new IndexWork.ManifestTouched(assetDir), Ct);
        Assert.Null(await _index.GetAsset("binance", "BTCUSDT_perp", Ct));
    }

    [Fact]
    public async Task Rebuild_DelegatesToRebuilderWithJobId()
    {
        var rebuilder = Substitute.For<IIndexRebuilder>();
        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = _root });
        var processor = new IndexWorkProcessor(_index, new FeedMonthScanner(), _schema, _statusStore,
            rebuilder, options, NullLogger<IndexWorkProcessor>.Instance);

        await processor.Process(new IndexWork.Rebuild("job-1"), Ct);

        await rebuilder.Received(1).Run("job-1", Arg.Any<CancellationToken>());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}
