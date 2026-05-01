namespace AlgoTradeForge.HistoryLoader.Tests.TestHelpers;

/// <summary>
/// Manual <see cref="TimeProvider"/> for tests. Constructed paused at <c>start</c>; advance
/// time explicitly via <see cref="Advance"/>. Lets retention / dedup / SSE-410 tests run in
/// milliseconds instead of waiting for wall-clock minutes.
/// </summary>
/// <remarks>
/// Hand-rolled rather than pulling in <c>Microsoft.Extensions.TimeProvider.Testing</c> per
/// CLAUDE.md "no new NuGet packages" — the surface needed is small.
/// </remarks>
public sealed class TestClock : TimeProvider
{
    private long _utcTicks;

    public TestClock(DateTimeOffset start)
    {
        _utcTicks = start.UtcTicks;
    }

    public override DateTimeOffset GetUtcNow() => new(_utcTicks, TimeSpan.Zero);

    public override long GetTimestamp() => _utcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan delta) => _utcTicks += delta.Ticks;
}
