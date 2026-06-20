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
        var tradesDir = Path.Combine(root, "ESZ5", "trades");
        var quotesDir = Path.Combine(root, "ESZ5", "quotes");
        Directory.CreateDirectory(tradesDir);
        Directory.CreateDirectory(quotesDir);

        var tradeSeg = Path.Combine(tradesDir, "0001700000000000-0000000000000000001.atft");
        var quoteSeg = Path.Combine(quotesDir, "0001700000000000-0000000000000000002.atft");
        var tradePayload = new byte[] { 1, 2, 3, 4, 5 };
        var quotePayload = new byte[] { 6, 7, 8 };
        await File.WriteAllBytesAsync(tradeSeg, tradePayload, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(quoteSeg, quotePayload, TestContext.Current.CancellationToken);

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

        Assert.Equal(2, first);
        Assert.Equal(0, second); // markers prevent re-upload

        const string tradeKey = "live-md/ib/ticks/ESZ5/trades/0001700000000000-0000000000000000001.atft";
        const string quoteKey = "live-md/ib/ticks/ESZ5/quotes/0001700000000000-0000000000000000002.atft";
        Assert.True(captured.ContainsKey(tradeKey));
        Assert.True(captured.ContainsKey(quoteKey));
        Assert.Equal(tradePayload, captured[tradeKey]);
        Assert.Equal(quotePayload, captured[quoteKey]);

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task SweepOnce_FailedUpload_LeavesNoMarker_AndRetries()
    {
        var root = Path.Combine(Path.GetTempPath(), $"upl_{Guid.NewGuid():N}");
        var streamDir = Path.Combine(root, "NQZ5", "trades");
        Directory.CreateDirectory(streamDir);
        await File.WriteAllBytesAsync(Path.Combine(streamDir, "0001700000000000-0000000000000000002.atft"),
            new byte[] { 9 }, TestContext.Current.CancellationToken);

        var storage = Substitute.For<IFileStorage>();
        storage.WriteAllBytes(Arg.Any<string>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new IOException("S3 down"));

        var uploader = new SegmentUploader(storage, root, "live-md/ib/ticks");

        await Assert.ThrowsAsync<IOException>(() => uploader.SweepOnce(TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(streamDir, "*.uploaded"));

        Directory.Delete(root, recursive: true);
    }
}
