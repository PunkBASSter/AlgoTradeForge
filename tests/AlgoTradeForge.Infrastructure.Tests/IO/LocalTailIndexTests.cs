using System.Text;
using AlgoTradeForge.Application.IO;
using AlgoTradeForge.Infrastructure.IO;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.IO;

public sealed class LocalTailIndexTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileStorage _storage;
    private readonly LocalTailIndex _tail;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public LocalTailIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"TailIndex_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _root });
        _tail = new LocalTailIndex(_storage);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task GetLastTimestamp_ReadsTimestampFromLastLine()
    {
        const string key = "partition.csv";
        await _storage.WriteAllLines(key, new[]
        {
            "ts,open,high,low,close",
            "1700000000000,1,2,0,1",
            "1700003600000,2,3,1,2",
            "1700007200000,3,4,2,3",
        }, Ct);

        Assert.Equal(1700007200000L, await _tail.GetLastTimestamp(key, Ct));
    }

    [Fact]
    public async Task GetLastTimestamp_ReturnsNull_ForMissingKey()
    {
        Assert.Null(await _tail.GetLastTimestamp("absent.csv", Ct));
    }

    [Fact]
    public async Task GetLastTimestamp_ReturnsNull_ForEmptyFile()
    {
        const string key = "empty.csv";
        await _storage.WriteAllBytes(key, ReadOnlyMemory<byte>.Empty, Ct);

        Assert.Null(await _tail.GetLastTimestamp(key, Ct));
    }

    [Fact]
    public async Task GetLastTimestamp_HandlesTrailingNewline()
    {
        const string key = "trailing-newline.csv";
        await _storage.WriteAllBytes(key, Encoding.UTF8.GetBytes("ts,v\n1234,7\n"), Ct);

        Assert.Equal(1234L, await _tail.GetLastTimestamp(key, Ct));
    }

    [Fact]
    public async Task GetLastTimestamp_ReturnsNull_WhenLastLineHasNoNumericTimestamp()
    {
        const string key = "header-only.csv";
        await _storage.WriteAllBytes(key, Encoding.UTF8.GetBytes("ts,v\n"), Ct);

        Assert.Null(await _tail.GetLastTimestamp(key, Ct));
    }

    [Fact]
    public async Task GetLastLine_ReturnsLastNonEmptyRow()
    {
        const string key = "tick-partition.csv";
        await _storage.WriteAllLines(key, new[]
        {
            "ts,price,qty,is_buyer_maker,agg_id",
            "1700000000000,100,1,0,42",
            "1700000000005,101,2,1,43",
        }, Ct);

        Assert.Equal("1700000000005,101,2,1,43", await _tail.GetLastLine(key, Ct));
    }

    [Fact]
    public async Task GetLastLine_ReturnsNull_ForMissingKey()
    {
        Assert.Null(await _tail.GetLastLine("absent.csv", Ct));
    }

    [Fact]
    public async Task GetLastLine_StripsTrailingNewlines()
    {
        const string key = "trailing-crlf.csv";
        await _storage.WriteAllBytes(key, Encoding.UTF8.GetBytes("ts,v\r\n1234,7\r\n"), Ct);

        Assert.Equal("1234,7", await _tail.GetLastLine(key, Ct));
    }
}
