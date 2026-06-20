namespace IbPoc;

public sealed record TradeTick(long EpochMs, double Price, decimal Size);

public sealed record Candle(
    long BucketStartMs, double Open, double High, double Low, double Close,
    decimal Volume, int TickCount);

public sealed class CandleAggregator(int bucketSeconds)
{
    private readonly long _bucketMs = bucketSeconds * 1000L;
    private long _bucketStart = -1;
    private double _open, _high, _low, _close;
    private decimal _volume;
    private int _count;

    public Candle? Add(TradeTick tick)
    {
        var bucket = (tick.EpochMs / _bucketMs) * _bucketMs;
        if (_bucketStart < 0)
        {
            StartBucket(bucket, tick);
            return null;
        }
        if (bucket != _bucketStart)
        {
            var emitted = Snapshot();
            StartBucket(bucket, tick);
            return emitted;
        }
        _high = Math.Max(_high, tick.Price);
        _low = Math.Min(_low, tick.Price);
        _close = tick.Price;
        _volume += tick.Size;
        _count++;
        return null;
    }

    public Candle? Flush()
    {
        if (_bucketStart < 0) return null;
        var c = Snapshot();
        _bucketStart = -1;
        return c;
    }

    private void StartBucket(long bucket, TradeTick tick)
    {
        _bucketStart = bucket;
        _open = _high = _low = _close = tick.Price;
        _volume = tick.Size;
        _count = 1;
    }

    private Candle Snapshot() =>
        new(_bucketStart, _open, _high, _low, _close, _volume, _count);
}
