using System.Text;
using AlgoTradeForge.Storage;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.IO;

/// <summary>Contract suite for any <see cref="IFileStorage"/> impl; subclasses inherit to run against their backend.</summary>
public abstract class FileStorageContractTests : IDisposable
{
    protected abstract IFileStorage Storage { get; }
    protected abstract string Prefix { get; }
    public abstract void Dispose();

    private string Key(string name) => $"{Prefix}/{name}";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<string>> Collect(IAsyncEnumerable<string> source)
    {
        var result = new List<string>();
        await foreach (var item in source) result.Add(item);
        return result;
    }

    [Fact]
    public async Task RoundTripText_ReturnsSameContent()
    {
        var key = Key("hello.txt");
        await Storage.WriteAllText(key, "hello world", Encoding.UTF8, Ct);

        Assert.True(await Storage.Exists(key, Ct));
        Assert.Equal("hello world", await Storage.ReadAllText(key, Ct));
    }

    [Fact]
    public async Task RoundTripBytes_ReturnsSameContent()
    {
        var key = Key("blob.bin");
        var payload = new byte[] { 0, 1, 2, 3, 4, 5, 250, 251, 252, 253, 254, 255 };
        await Storage.WriteAllBytes(key, payload, Ct);

        Assert.Equal(payload, await Storage.ReadAllBytes(key, Ct));
    }

    [Fact]
    public async Task WriteAllLines_ReadAllLines_PreservesOrder()
    {
        var key = Key("lines.csv");
        var lines = new[] { "a,1", "b,2", "c,3" };
        await Storage.WriteAllLines(key, lines, Ct);

        Assert.Equal(lines, await Storage.ReadAllLines(key, Ct));
    }

    [Fact]
    public async Task ReadLines_Streams()
    {
        var key = Key("stream.csv");
        await Storage.WriteAllLines(key, new[] { "x", "y", "z" }, Ct);

        var collected = await Collect(Storage.ReadLines(key, Ct));
        Assert.Equal(new[] { "x", "y", "z" }, collected);
    }

    [Fact]
    public async Task Exists_ReturnsFalse_ForMissingKey()
    {
        Assert.False(await Storage.Exists(Key("does-not-exist.txt"), Ct));
    }

    [Fact]
    public async Task ListKeys_FiltersBySuffix()
    {
        await Storage.WriteAllText(Key("list/a.csv"), "1", ct: Ct);
        await Storage.WriteAllText(Key("list/b.csv"), "2", ct: Ct);
        await Storage.WriteAllText(Key("list/c.txt"), "3", ct: Ct);

        var csvKeys = await Collect(Storage.ListKeys(Key("list"), suffix: ".csv", ct: Ct));
        Assert.Equal(2, csvKeys.Count);
        Assert.All(csvKeys, k => Assert.EndsWith(".csv", k));
    }

    [Fact]
    public async Task ListKeys_NonRecursive_ReturnsOnlyDirectChildren()
    {
        await Storage.WriteAllText(Key("flat/top.csv"), "1", ct: Ct);
        await Storage.WriteAllText(Key("flat/nested/deep.csv"), "2", ct: Ct);

        var topOnly = await Collect(Storage.ListKeys(Key("flat"), recursive: false, ct: Ct));
        Assert.Single(topOnly);
    }

    [Fact]
    public async Task OpenWriteSession_ExplicitCommit_PublishesAtomically()
    {
        var key = Key("session-commit.txt");
        await using (var session = await Storage.OpenWriteSession(key, Ct))
        {
            await session.Stream.WriteAsync(Encoding.UTF8.GetBytes("committed"), Ct);
            await session.Commit(Ct);
        }

        Assert.True(await Storage.Exists(key, Ct));
        Assert.Equal("committed", await Storage.ReadAllText(key, Ct));
    }

    [Fact]
    public async Task OpenWriteSession_ExplicitAbort_LeavesNoVisibleObject()
    {
        var key = Key("session-abort.txt");
        await using (var session = await Storage.OpenWriteSession(key, Ct))
        {
            await session.Stream.WriteAsync(Encoding.UTF8.GetBytes("should not be visible"), Ct);
            await session.Abort(Ct);
        }

        Assert.False(await Storage.Exists(key, Ct));
    }

    [Fact]
    public async Task OpenWriteSession_DisposeWithoutCommit_LeavesNoVisibleObject()
    {
        // Default-abort protects against partial publish on cancellation/exception mid-write.
        var key = Key("session-implicit-abort.txt");
        await using (var session = await Storage.OpenWriteSession(key, Ct))
        {
            await session.Stream.WriteAsync(Encoding.UTF8.GetBytes("forgot to commit"), Ct);
        }

        Assert.False(await Storage.Exists(key, Ct));
    }

    [Fact]
    public async Task OpenWriteSession_OverwritesExistingObject()
    {
        var key = Key("overwrite.txt");
        await Storage.WriteAllText(key, "v1", Encoding.UTF8, Ct);

        await using (var session = await Storage.OpenWriteSession(key, Ct))
        {
            await session.Stream.WriteAsync(Encoding.UTF8.GetBytes("v2"), Ct);
            await session.Commit(Ct);
        }

        Assert.Equal("v2", await Storage.ReadAllText(key, Ct));
    }

    [Fact]
    public async Task Move_RenamesKey()
    {
        var src = Key("move-src.txt");
        var dst = Key("move-dst.txt");
        await Storage.WriteAllText(src, "payload", Encoding.UTF8, Ct);

        await Storage.Move(src, dst, overwrite: false, ct: Ct);

        Assert.False(await Storage.Exists(src, Ct));
        Assert.True(await Storage.Exists(dst, Ct));
        Assert.Equal("payload", await Storage.ReadAllText(dst, Ct));
    }

    [Fact]
    public async Task Delete_RemovesObject()
    {
        var key = Key("delete-me.txt");
        await Storage.WriteAllText(key, "x", Encoding.UTF8, Ct);

        await Storage.Delete(key, Ct);

        Assert.False(await Storage.Exists(key, Ct));
    }

    [Fact]
    public async Task Delete_IsNoOpForMissingKey()
    {
        // Should not throw — idempotent semantics.
        await Storage.Delete(Key("never-existed.txt"), Ct);
    }

    [Fact]
    public async Task DeleteByPrefix_ClearsNestedKeys()
    {
        await Storage.WriteAllText(Key("zone/a.csv"), "1", ct: Ct);
        await Storage.WriteAllText(Key("zone/sub/b.csv"), "2", ct: Ct);
        await Storage.WriteAllText(Key("zone/sub/c.csv"), "3", ct: Ct);

        await Storage.DeleteByPrefix(Key("zone"), Ct);

        Assert.Empty(await Collect(Storage.ListKeys(Key("zone"), ct: Ct)));
    }
}
