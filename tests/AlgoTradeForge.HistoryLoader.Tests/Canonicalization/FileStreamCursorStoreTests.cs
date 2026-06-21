using AlgoTradeForge.Storage;
using AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Canonicalization;

public sealed class FileStreamCursorStoreTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileStorage _storage;
    private readonly FileStreamCursorStore _store;
    private const string Key = "_canon-cursors/binance/BTCUSDT/trades.cursor";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public FileStreamCursorStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"CursorStoreTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _root });
        _store = new FileStreamCursorStore(_storage);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task Read_Absent_ReturnsEmptyCursor()
    {
        var c = await _store.Read(Key, Ct);
        Assert.Null(c.LastSegmentKey);
        Assert.Null(c.ETag);
    }

    [Fact]
    public async Task Advance_ThenRead_RoundTrips()
    {
        var etag = await _store.Advance(Key, "live-md/binance/BTCUSDT/trades/a.atft", expectedETag: null, Ct);
        var c = await _store.Read(Key, Ct);
        Assert.Equal("live-md/binance/BTCUSDT/trades/a.atft", c.LastSegmentKey);
        Assert.Equal(etag, c.ETag);
    }

    [Fact]
    public async Task Advance_StaleEtag_ThrowsConcurrencyConflict()
    {
        var etag1 = await _store.Advance(Key, "seg-a", expectedETag: null, Ct);
        await _store.Advance(Key, "seg-b", expectedETag: etag1, Ct); // moves etag forward

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => _store.Advance(Key, "seg-c", expectedETag: etag1, Ct)); // stale
    }
}
