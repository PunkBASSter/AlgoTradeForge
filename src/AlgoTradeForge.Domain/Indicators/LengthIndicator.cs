using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Indicators;

public abstract class LengthIndicator<T> : BaseIndicator
{
    private int _length;

    public int Length
    {
        get => _length;
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Length must be positive.");

            if (_length == value)
                return;

            _length = value;
            Reset();
        }
    }

    public TimeSeries<T> Buffer { get; private set; }

    public override int NumValuesToInitialize => Length;

    protected LengthIndicator(int length)
    {
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, "Length must be positive.");

        _length = length;
        Buffer = CreateBuffer();
    }

    protected override bool CalcIsFormed() => Buffer.Count >= Length;

    protected override void OnReset()
    {
        Buffer = CreateBuffer();
    }

    private TimeSeries<T> CreateBuffer()
        => new(DateTimeOffset.MinValue, TimeSpan.FromTicks(1), capacity: _length);
}

public abstract class LongLengthIndicator(int length) : LengthIndicator<long>(length);
