using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.Storage;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Live.Relay.Tests;

public class SegmentUploaderTests
{
    [Fact]
    public async Task SweepOnce_UploadsEachSegmentOnce_WithIdenticalBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"upl_{Guid.NewGuid():N}");
        var instrDir = Path.Combine(root, "ESZ5");
        Directory.CreateDirectory(instrDir);
        var segPath = Path.Combine(instrDir, "0001700000000000-0000000000000000001.atft");
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        await File.WriteAllBytesAsync(segPath, payload, TestContext.Current.CancellationToken);

        var captured = new Dictionary<string, byte[]>();
        var storage = Substitute.For<IFileStorage>();
        storage.WriteAllBytes(Arg.Any<string>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured[ci.ArgAt<string>(0)] = ci.ArgAt<ReadOnlyMemory<byte>>(1).ToArray();
                return Task.CompletedTask;
            });

        var uploader = new SegmentUploader(storage, root, keyPrefix: "live-md/ib/ticks");

        var first = await uploader.SweepOnce(TestContext.Current.CancellationToken);
        var second = await uploader.SweepOnce(TestContext.Current.CancellationToken);

        Assert.Equal(1, first);
        Assert.Equal(0, second); // marker prevents re-upload
        Assert.True(captured.ContainsKey("live-md/ib/ticks/ESZ5/0001700000000000-0000000000000000001.atft"));
        Assert.Equal(payload, captured["live-md/ib/ticks/ESZ5/0001700000000000-0000000000000000001.atft"]);

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task SweepOnce_FailedUpload_LeavesNoMarker_AndRetries()
    {
        var root = Path.Combine(Path.GetTempPath(), $"upl_{Guid.NewGuid():N}");
        var instrDir = Path.Combine(root, "NQZ5");
        Directory.CreateDirectory(instrDir);
        await File.WriteAllBytesAsync(Path.Combine(instrDir, "0001700000000000-0000000000000000002.atft"),
            new byte[] { 9 }, TestContext.Current.CancellationToken);

        var storage = Substitute.For<IFileStorage>();
        storage.WriteAllBytes(Arg.Any<string>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new IOException("S3 down"));

        var uploader = new SegmentUploader(storage, root, "live-md/ib/ticks");

        await Assert.ThrowsAsync<IOException>(() => uploader.SweepOnce(TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(instrDir, "*.uploaded"));

        Directory.Delete(root, recursive: true);
    }
}
