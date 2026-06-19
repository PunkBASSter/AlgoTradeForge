using IbPoc;
using Xunit;

public class CandleAggregatorTests
{
    [Fact]
    public void Add_TicksInSameBucket_ReturnsNullAndDoesNotEmit()
    {
        var agg = new CandleAggregator(bucketSeconds: 5);
        Assert.Null(agg.Add(new TradeTick(0, 10.0, 1)));
        Assert.Null(agg.Add(new TradeTick(2_000, 11.0, 2)));   // same 5s bucket [0,5000)
    }

    [Fact]
    public void Add_TickInNewBucket_EmitsCompletedPriorCandleWithOHLCV()
    {
        var agg = new CandleAggregator(bucketSeconds: 5);
        agg.Add(new TradeTick(0, 10.0, 1));
        agg.Add(new TradeTick(1_000, 12.0, 2));
        agg.Add(new TradeTick(2_000, 9.0, 3));
        var emitted = agg.Add(new TradeTick(5_000, 20.0, 1)); // rolls into bucket [5000,10000)
        Assert.NotNull(emitted);
        Assert.Equal(0, emitted!.BucketStartMs);
        Assert.Equal(10.0, emitted.Open);
        Assert.Equal(12.0, emitted.High);
        Assert.Equal(9.0, emitted.Low);
        Assert.Equal(9.0, emitted.Close);
        Assert.Equal(6m, emitted.Volume);
        Assert.Equal(3, emitted.TickCount);
    }

    [Fact]
    public void Flush_WithInProgressBucket_EmitsIt()
    {
        var agg = new CandleAggregator(bucketSeconds: 5);
        agg.Add(new TradeTick(0, 10.0, 1));
        var flushed = agg.Flush();
        Assert.NotNull(flushed);
        Assert.Equal(10.0, flushed!.Close);
        Assert.Null(agg.Flush()); // nothing left
    }
}
