using AlgoTradeForge.Storage;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;
using AlgoTradeForge.HistoryLoader.Infrastructure.State;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Index;

public sealed class IndexRebuilderTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("atf-rebuild-").FullName;
    private SqliteHistoryIndex _index = null!;
    private IndexRebuilder _rebuilder = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var init = new HistoryIndexInitializer(Path.Combine(_root, "idx.sqlite"));
        await init.EnsureCreated();
        _index = new SqliteHistoryIndex(init, init.ConnectionString + ";Pooling=False");

        var dataRoot = Path.Combine(_root, "data");
        SeedAsset(dataRoot, "binance", "BTCUSDT", intervals: ["1h"], monthRows: ("2024-01", 744));

        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = dataRoot });

        // Real components over the temp tree. LocalFileStorage + FeedSchemaManager +
        // FeedStatusManager are the production read path — construct them the same way
        // AddHistoryLoaderInfrastructure does (adjust ctor args to actual signatures).
        var storage = new LocalFileStorage();
        var schema = new FeedSchemaManager(storage);
        var statusStore = new FeedStatusManager(storage);

        _rebuilder = new IndexRebuilder(storage, options, schema, statusStore,
            new FeedMonthScanner(), _index, NullLogger<IndexRebuilder>.Instance);
    }

    private static void SeedAsset(string dataRoot, string exchange, string dir,
        string[] intervals, (string Month, int Rows) monthRows)
    {
        var assetDir = Path.Combine(dataRoot, exchange, dir);
        Directory.CreateDirectory(Path.Combine(assetDir, "candles"));
        var intervalsJson = string.Join(",", intervals.Select(i => $"\"{i}\""));
        File.WriteAllText(Path.Combine(assetDir, "feeds.json"),
            $"{{\"feeds\":{{}},\"candles\":{{\"multiplier\":100,\"intervals\":[{intervalsJson}]}}}}");
        File.WriteAllLines(Path.Combine(assetDir, "candles", $"{monthRows.Month}_{intervals[0]}.csv"),
            new[] { "ts,o,h,l,c,v" }.Concat(Enumerable.Range(0, monthRows.Rows).Select(i => $"{i},1,1,1,1,1")));
        File.WriteAllText(Path.Combine(assetDir, "candles", $"status_{intervals[0]}.json"),
            """{"feedName":"candles","interval":"1h","firstTimestamp":1,"lastTimestamp":2,"recordCount":744,"gaps":[],"health":0,"completeMonths":[]}""");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Run_IndexesAssetsStatusesAndMonths_AndCompletesJob()
    {
        var jobId = await _index.CreateJob("rebuild", Ct);
        await _rebuilder.Run(jobId, Ct);

        var asset = Assert.Single(await _index.ListAssets(ct: Ct));
        Assert.Equal(("binance", "BTCUSDT"), (asset.Exchange, asset.Dir));
        var month = Assert.Single(await _index.GetMonths("binance", "BTCUSDT", "candles", "1h", Ct));
        Assert.Equal(744, month.Rows);
        Assert.Equal("completed", (await _index.GetJob(jobId, Ct))!.State);
    }

    [Fact]
    public async Task Run_PrunesRowsForAssetsGoneFromDisk()
    {
        await _index.UpsertAsset(new("binance", "GHOST", "GHOST", "Crypto", "{}"), Ct);
        var jobId = await _index.CreateJob("rebuild", Ct);

        await _rebuilder.Run(jobId, Ct);

        Assert.Null(await _index.GetAsset("binance", "GHOST", Ct));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}
