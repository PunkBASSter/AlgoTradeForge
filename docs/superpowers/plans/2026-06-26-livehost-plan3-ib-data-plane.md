# LiveHost Plan 3 — IbVenueConnector (data plane) + single-session IbSession — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Commits are CONTROLLER-performed.** The implementer subagent's `git add` is hook-denied; leave changes uncommitted. The controller stages + commits after each task's review passes. The "Commit" step in each task documents the intended commit — the subagent does NOT run it.

**Goal:** Build the Interactive Brokers market-data plane for LiveHost — a single shared `IbSession` over the Plan-1 transport, an `IbVenueConnector` bridging tick-by-tick callbacks to the relay seam, an `IbVenueBarSource` for 5s venue bars, and lossless reconnect recovery via time-based catch-up + real `reqHistoricalTicks` backfill — driving a config-selectable `LiveHost@ib`.

**Architecture:** Two data lanes (ticks → relay/archival/tick-router; 5s bars → dispatch) ride one `EClientSocket` owned by `IbSession`, grown around the Plan-1 `IbConnection` + `IbWrapper`. `IbWrapper` demuxes every `EWrapper` callback back to the right lane by `reqId`. The relay seam (`IVenueConnector`, `RelayIngest.Pump`, `IMarketEvent`), `TickRouter`, and `StrategyDispatch` are untouched — IB enters through the doors Binance already uses.

**Tech Stack:** C# 14 / .NET 10; vendored `IBApi` 10.45.01 (`src/AlgoTradeForge.IbApi`, nullable-off, Google.Protobuf 3.29.5); xUnit + NSubstitute; `System.Threading.Channels`; `AlgoTradeForge.Live.Relay` segment format (`.atft`).

## Global Constraints

- ONE `dotnet` process at a time — build/test strictly sequential, never parallel. `powershell.exe` only (no `pwsh` on this machine).
- All new IB types are `internal`. The `IBApi` reference is confined to the connector/translation/wrapper/transport seam — it must not leak into `IVenueConnector`/`IBarSource` consumers.
- Domain stays venue-neutral with ZERO new ProjectReferences. LiveHost must not depend on HistoryLoader; `Live.Relay` must not depend on LiveHost.
- Int64 money: `MoneyConvert.ToLong` in Domain; the IB connector does **independent** price/qty scaling at its own boundary (configured per-instrument exponents, mirror `TickScale`). Never raw `(long)` casts for money.
- No `Async` suffix on new async methods. One type per file (single-line companion records / private nested types may co-locate). Prefer `using` over `try`/`finally` for pure release.
- No `catch when (ex is not OperationCanceledException)` in long-running loops — use `IsTrueShutdown(ex, ct)`.
- Every channel bounded. The bar lane is independent of the tick lane; the (future Plan-4) order path stays independent of market data.
- xUnit1051 is enforced as an **error**: every awaited call in a test passes a `CancellationToken` — use `TestContext.Current.CancellationToken`.
- NSubstitute mocking an internal interface relies on `InternalsVisibleTo("DynamicProxyGenAssembly2")` — already present in `AlgoTradeForge.LiveHost.Infrastructure.csproj`.
- IB time units: `tickByTickAllLast.time`, `realtimeBar.date`, `HistoricalTickLast.Time` are **Unix seconds**; Domain `TimestampMs`/`OpenTime` are **milliseconds** → ×1000 at the boundary.
- IB `tickByTickAllLast` has no maker/taker flag and no exchange sequence: map `Aggressor = AggressorSide.Unknown` and a synthetic per-instrument monotonic `Sequence` (archive ordering only; NOT a gap signal).

## Build & test commands

```bash
# Build the public solution
dotnet build AlgoTradeForge.slnx

# Unit tests touched by this plan (run sequentially)
dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/
dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/
dotnet test tests/AlgoTradeForge.LiveHost.WebApi.Tests/
```

## Reference signatures (existing code — consume, do not redefine)

```csharp
// AlgoTradeForge.Live.Relay
public interface IVenueConnector {
    string Venue { get; }
    MarketDataSessionPolicy SessionPolicy { get; }
    (sbyte PriceScaleExp, sbyte QtyScaleExp) InstrumentScale(string instrument);
    IAsyncEnumerable<IMarketEvent> Stream(IReadOnlyList<string> instruments, CancellationToken ct = default);
}
public interface IMarketEvent { long TimestampMs { get; } }
public sealed record TradeEvent(string Instrument, TradeTick Tick) : IMarketEvent;
public enum MarketDataSessionPolicy { Concurrent, SingleSession }
public sealed class SegmentWriter<T> where T : IFramePayload<T> { SegmentWriter(Stream dest, in SegmentHeader header, bool leaveOpen=false); void Write(in T payload); void Dispose(); }
public readonly record struct SegmentHeader(sbyte PriceScaleExp, sbyte QtyScaleExp, long EpochBaseMs, long CreatedAtMs, long FirstSequence, ushort PayloadSize) { const int Size=64; }

// AlgoTradeForge.Domain.History
public readonly record struct TradeTick(long TimestampMs, long Price, long Quantity, long Sequence, AggressorSide Aggressor) : IFramePayload<TradeTick> { static int PayloadSize => 33; }
public enum AggressorSide : byte { Unknown = 0, Buy = 1, Sell = 2 }
public readonly record struct Int64Bar(long OpenTime/*aka TimestampMs*/, long Open, long High, long Low, long Close, long Volume);

// AlgoTradeForge.LiveHost.Application.Live.DataPlane
public interface IBarSource { IReadOnlyList<Int64Bar> Recent { get; } Task Start() => Task.CompletedTask; }
public interface ITickDrivenBarSource : IBarSource { void Feed(in TradeTick tick); }
public interface IBarSourceResolver { IBarSource? Resolve(string instrument, DataFeedSubscription subscription, ScaleContext scale, Action<Int64Bar,bool> onBar); }

// AlgoTradeForge.LiveHost.Application.Live.Recovery
public interface ICatchupGate { bool Seeded { get; } long LastTimestampMs { get; } TickAdmission Admit(in TradeTick tick); void Reseed(in TradeTick tick); }
public enum TickAdmission { Accept, Duplicate, Gap }
public interface IBackfillRequester { Task<bool> TryBackfill(ReplayRequest context, Discontinuity gap, RecoveryPolicy policy, CancellationToken ct = default); }
public sealed class CatchupCoordinator(IReplaySource replay, IBackfillRequester backfill, RecoveryPolicy policy) { IAsyncEnumerable<TradeTick> StreamFromBoundary(ReplayRequest request, ICatchupGate gate, Action<Discontinuity> onDiscontinuity, CancellationToken ct=default); }
public readonly record struct ReplayRequest(Asset Asset, string Venue, string SourceFeedId, long FromTs);
public readonly record struct Discontinuity(long FromTs, long ToTs, DiscontinuityReason Reason);
public sealed record RecoveryPolicy(TimeSpan BackfillBudget, TimeSpan PollInterval);

// AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers  (Plan 1 — existing)
internal enum IbSecType { Stk, Fut }
internal readonly record struct IbContract(string Symbol, IbSecType SecType, string Exchange, string PrimaryExch, string Currency);
internal sealed record ResolvedIbContract(IbContract Spec, int ConId, string LocalSymbol, string LastTradeDate);
internal interface IIbContractResolver { Task<ResolvedIbContract> Resolve(IbContract spec, CancellationToken ct = default); }
internal static class IbContractMapping { static IbContract ToIbContract(this Asset asset); /* + ToAsset */ }
internal static class IbContractTranslation { static IBApi.Contract ToIbApiContract(this IbContract c); /* and ResolvedIbContract overload if present */ }
internal sealed record IbConnectionOptions(string Host, int Port, int ClientId);
internal sealed class IbConnection(IbWrapper wrapper, IbConnectionOptions options) : IAsyncDisposable { EClientSocket Client { get; } Task Connect(int maxAttempts=90, int retryDelayMs=2000, CancellationToken ct=default); void Disconnect(); ValueTask DisposeAsync(); }
internal sealed class IbWrapper : DefaultEWrapper { Task<int> NextValidId { get; } ContractDetailsRequest RegisterContractDetails(int reqId); void ReleaseContractDetails(int reqId); /* overrides: nextValidId, contractDetails, contractDetailsEnd, error */ }
```

> **Verified IB callback/request signatures (vendored 10.45.01):**
> `tickByTickAllLast(int reqId, int tickType, long time, double price, decimal size, TickAttribLast tickAttribLast, string exchange, string specialConditions)`;
> `realtimeBar(int reqId, long time, double open, double high, double low, double close, decimal volume, decimal WAP, int count)`;
> `historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done)`; `connectionClosed()`; `error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)`.
> `reqTickByTickData(int requestId, Contract contract, string tickType, int numberOfTicks, bool ignoreSize)`;
> `reqRealTimeBars(int tickerId, Contract contract, int barSize, string whatToShow, bool useRTH, List<TagValue> realTimeBarsOptions)`;
> `reqHistoricalTicks(int reqId, Contract contract, string startDateTime, string endDateTime, int numberOfTicks, string whatToShow, int useRth, bool ignoreSize, List<TagValue> miscOptions)`;
> `cancelTickByTickData(int requestId)`; `cancelRealTimeBars(int tickerId)`. `HistoricalTickLast` fields: `long Time` (seconds), `double Price`, `decimal Size`.

## File structure

**New — `src/AlgoTradeForge.LiveHost.Application/Live/Recovery/`**
- `TimeWatermarkGate.cs` — `ICatchupGate` keyed on timestamp jumps (IB).

**Modified — `src/AlgoTradeForge.LiveHost.Application/Live/DataPlane/`**
- `TickAggregationBarSource.cs` — make the catch-up gate an injected ctor param.

**New — `src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/`**
- `IbTradeUpdate.cs`, `IbRealtimeBar.cs`, `IbHistoricalTick.cs` — raw IB market-data DTO record structs (seconds, double/decimal).
- `IbWrapper.cs` *(modified)* — tick/bar sink demux, historical-ticks correlator, reconnect signal, resettable nextValidId.
- `IbConnection.cs` *(modified)* — pump Join on teardown; reconnect-capable.
- `IIbMarketDataClient.cs` + `IbConnectionMarketDataClient.cs` — issue/cancel tick & realtime-bar requests + Connect over the socket.
- `IbSession.cs` — shared session: reqId alloc, typed subscribe, active-subscription tracking, reconnect → re-subscribe.
- `IbVenueConnector.cs` — `IVenueConnector` tick lane.
- `IbVenueBarSource.cs` — `IBarSource` 5s venue-bar lane.
- `IIbHistoricalTicksClient.cs` + `IbConnectionHistoricalTicksClient.cs` — `reqHistoricalTicks` paged fetch.
- `IbBackfillRequester.cs` — `IBackfillRequester` writing fetched ticks to the relay archive.
- `IbBarSourceResolver.cs` — IB `IBarSourceResolver`.
- `IbDataPlaneOptions.cs` — host/port/clientId + per-instrument scale + per-instrument contract config.
- `VenueKind.cs` + `VenueSelector.cs` — config → active-venue selection.

**Modified**
- `src/AlgoTradeForge.LiveHost.WebApi/Program.cs` — `Venue` config branch registering the IB trio vs the Binance trio.

**New tests — `tests/AlgoTradeForge.LiveHost.Application.Tests/Live/Recovery/`** and **`.../DataPlane/`**, **`tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers/`**, **`tests/AlgoTradeForge.LiveHost.WebApi.Tests/`** (`IbRoundTripTests.cs`).

---

### Task 1: Injectable catch-up gate + `TimeWatermarkGate`

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Application/Live/Recovery/TimeWatermarkGate.cs`
- Modify: `src/AlgoTradeForge.LiveHost.Application/Live/DataPlane/TickAggregationBarSource.cs:19,37-51`
- Test: `tests/AlgoTradeForge.LiveHost.Application.Tests/Live/Recovery/TimeWatermarkGateTests.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Application.Tests/Live/DataPlane/TickAggregationBarSourceGateTests.cs`

**Interfaces:**
- Consumes: `ICatchupGate`, `TickAdmission`, `TradeTick`.
- Produces: `public sealed class TimeWatermarkGate(long maxGapMs) : ICatchupGate`; `TickAggregationBarSource` ctor gains a trailing `ICatchupGate? gate = null` parameter (default `new SequenceWatermarkGate()`).

- [ ] **Step 1: Write the failing test (gate semantics)**

```csharp
// TimeWatermarkGateTests.cs
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using Xunit;

namespace AlgoTradeForge.LiveHost.Application.Tests.Live.Recovery;

public class TimeWatermarkGateTests
{
    private static TradeTick T(long ts) => new(ts, 100, 1, 0, AggressorSide.Unknown);

    [Fact]
    public void FirstTick_SeedsAndAccepts()
    {
        var g = new TimeWatermarkGate(maxGapMs: 1000);
        Assert.Equal(TickAdmission.Accept, g.Admit(T(5000)));
        Assert.True(g.Seeded);
        Assert.Equal(5000, g.LastTimestampMs);
    }

    [Fact]
    public void OlderTimestamp_IsDuplicate()
    {
        var g = new TimeWatermarkGate(1000);
        g.Admit(T(5000));
        Assert.Equal(TickAdmission.Duplicate, g.Admit(T(4999)));
    }

    [Fact]
    public void WithinMaxGap_Accepts_AndAdvances()
    {
        var g = new TimeWatermarkGate(1000);
        g.Admit(T(5000));
        Assert.Equal(TickAdmission.Accept, g.Admit(T(5800)));
        Assert.Equal(5800, g.LastTimestampMs);
    }

    [Fact]
    public void JumpBeyondMaxGap_IsGap_AndDoesNotAdvance()
    {
        var g = new TimeWatermarkGate(1000);
        g.Admit(T(5000));
        Assert.Equal(TickAdmission.Gap, g.Admit(T(7000)));
        Assert.Equal(5000, g.LastTimestampMs); // unchanged until Reseed
    }

    [Fact]
    public void Reseed_MovesWatermarkToTick()
    {
        var g = new TimeWatermarkGate(1000);
        g.Admit(T(5000));
        g.Reseed(T(7000));
        Assert.Equal(7000, g.LastTimestampMs);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/ --filter FullyQualifiedName~TimeWatermarkGateTests`
Expected: FAIL — `TimeWatermarkGate` does not exist (compile error).

- [ ] **Step 3: Implement `TimeWatermarkGate`**

```csharp
// TimeWatermarkGate.cs
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.LiveHost.Application.Live.Recovery;

/// <summary>
/// Time-venue <see cref="ICatchupGate"/> for venues without a contiguous per-instrument sequence
/// (IB tick-by-tick). Dedupes strictly-older ticks (the replay→live overlap) and reports a
/// <see cref="TickAdmission.Gap"/> when the inter-tick time jump exceeds <paramref name="maxGapMs"/>
/// — the disconnect signal. A quiet market can false-positive; the historical-backfill requester
/// makes a spurious gap harmless (it re-fetches and dedupes to nothing). Single-threaded: the
/// owning bar source serializes admission.
/// </summary>
public sealed class TimeWatermarkGate(long maxGapMs) : ICatchupGate
{
    private long _lastTs;
    public bool Seeded { get; private set; }
    public long LastTimestampMs => _lastTs;

    public TickAdmission Admit(in TradeTick tick)
    {
        if (!Seeded) { Seeded = true; _lastTs = tick.TimestampMs; return TickAdmission.Accept; }
        if (tick.TimestampMs < _lastTs) return TickAdmission.Duplicate;
        if (tick.TimestampMs - _lastTs > maxGapMs) return TickAdmission.Gap;
        _lastTs = tick.TimestampMs;
        return TickAdmission.Accept;
    }

    public void Reseed(in TradeTick tick) { Seeded = true; _lastTs = tick.TimestampMs; }
}
```

- [ ] **Step 4: Make the bar source gate injectable**

In `TickAggregationBarSource.cs`, change the field (line ~19) from an inline initializer to a ctor-assigned field, and add a trailing ctor parameter:

```csharp
// field
private readonly ICatchupGate _watermark;

// ctor signature — add `ICatchupGate? gate = null` AFTER catchup
public TickAggregationBarSource(
    string typeCode, long frozenThreshold, ScaleContext scale, Action<Int64Bar, bool> onBar,
    int recentCapacity = 256, CatchupPlan? catchup = null, ICatchupGate? gate = null)
{
    ArgumentNullException.ThrowIfNull(onBar);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recentCapacity);

    _acc = AccumulatorEntry.Open(typeCode, frozenThreshold, scale, scale, DataFeedKind.Tick);
    _onBar = onBar;
    _recentCapacity = recentCapacity;
    _recent = new Queue<Int64Bar>(recentCapacity);
    _catchup = catchup;
    _watermark = gate ?? new SequenceWatermarkGate();
    _phase = catchup is null ? Phase.Live : Phase.Cold;
}
```

- [ ] **Step 5: Write the failing test (injected gate is used)**

```csharp
// TickAggregationBarSourceGateTests.cs
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using Xunit;

namespace AlgoTradeForge.LiveHost.Application.Tests.Live.DataPlane;

public class TickAggregationBarSourceGateTests
{
    // A spy gate that records how many ticks it admitted, proving the injected instance is used.
    private sealed class CountingGate : ICatchupGate
    {
        public int Admitted { get; private set; }
        public bool Seeded { get; private set; }
        public long LastTimestampMs { get; private set; }
        public TickAdmission Admit(in TradeTick tick)
        { Admitted++; Seeded = true; LastTimestampMs = tick.TimestampMs; return TickAdmission.Accept; }
        public void Reseed(in TradeTick tick) { LastTimestampMs = tick.TimestampMs; }
    }

    [Fact]
    public void Feed_UsesInjectedGate()
    {
        var gate = new CountingGate();
        // EqV threshold large enough that no bar emits; we only assert the gate saw the tick.
        var src = new TickAggregationBarSource(
            "EqV", frozenThreshold: long.MaxValue, scale: new ScaleContext(tickSize: 0.01m),
            onBar: (_, _) => { }, gate: gate);

        src.Feed(new TradeTick(1000, 100, 1, 0, AggressorSide.Unknown));

        Assert.Equal(1, gate.Admitted);
    }
}
```

> Note: confirm `ScaleContext`'s public ctor — `new ScaleContext(tickSize)` per CLAUDE.md ("`new ScaleContext(tickSize)`"). If the test project already has a `ScaleContext` helper, reuse it.

- [ ] **Step 6: Run both test files**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/ --filter "FullyQualifiedName~TimeWatermarkGateTests|FullyQualifiedName~TickAggregationBarSourceGateTests"`
Expected: PASS (all).

- [ ] **Step 7: Regression — Binance catch-up untouched**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/`
Expected: PASS — the default `gate` arg keeps every existing call site (`BarSourceResolver`, M6 golden) on `SequenceWatermarkGate`.

- [ ] **Step 8: Commit** *(controller)*

```bash
git add src/AlgoTradeForge.LiveHost.Application/Live/Recovery/TimeWatermarkGate.cs \
        src/AlgoTradeForge.LiveHost.Application/Live/DataPlane/TickAggregationBarSource.cs \
        tests/AlgoTradeForge.LiveHost.Application.Tests/Live/
git commit -m "feat(livehost): time-based catch-up gate + injectable gate on TickAggregationBarSource"
```

---

### Task 2: IB market-data DTOs + `IbWrapper` tick/bar demux

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbTradeUpdate.cs`
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbRealtimeBar.cs`
- Modify: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbWrapper.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers/IbWrapperTests.cs` (extend)

**Interfaces:**
- Consumes: `DefaultEWrapper`, `TickAttribLast` (IBApi).
- Produces:
  - `internal readonly record struct IbTradeUpdate(long TimeSec, double Price, decimal Size);`
  - `internal readonly record struct IbRealtimeBar(long DateSec, double Open, double High, double Low, double Close, decimal Volume);`
  - `IbWrapper`: `void RegisterTickSink(int reqId, Action<IbTradeUpdate> sink)`, `void RegisterBarSink(int reqId, Action<IbRealtimeBar> sink)`, `void ReleaseMarketData(int reqId)`. The wrapper invokes the registered sink on `tickByTickAllLast` / `realtimeBar`.

- [ ] **Step 1: Write the failing tests (demux routing)**

```csharp
// append to IbWrapperTests.cs
[Fact]
public void TickByTickAllLast_RoutesToRegisteredTickSink()
{
    var w = new IbWrapper();
    IbTradeUpdate? seen = null;
    w.RegisterTickSink(20, u => seen = u);

    w.tickByTickAllLast(20, tickType: 1, time: 1_700_000_000L, price: 296.98, size: 3m,
        tickAttribLast: new IBApi.TickAttribLast(), exchange: "NASDAQ", specialConditions: "");

    Assert.NotNull(seen);
    Assert.Equal(1_700_000_000L, seen!.Value.TimeSec);
    Assert.Equal(296.98, seen.Value.Price);
    Assert.Equal(3m, seen.Value.Size);
}

[Fact]
public void RealtimeBar_RoutesToRegisteredBarSink()
{
    var w = new IbWrapper();
    IbRealtimeBar? seen = null;
    w.RegisterBarSink(21, b => seen = b);

    w.realtimeBar(21, time: 1_700_000_005L, open: 1.0, high: 2.0, low: 0.5, close: 1.5,
        volume: 10m, WAP: 1.2m, count: 4);

    Assert.NotNull(seen);
    Assert.Equal(1_700_000_005L, seen!.Value.DateSec);
    Assert.Equal(2.0, seen.Value.High);
    Assert.Equal(10m, seen.Value.Volume);
}

[Fact]
public void ReleaseMarketData_StopsRouting()
{
    var w = new IbWrapper();
    int calls = 0;
    w.RegisterTickSink(22, _ => calls++);
    w.ReleaseMarketData(22);
    w.tickByTickAllLast(22, 1, 1L, 1.0, 1m, new IBApi.TickAttribLast(), "", "");
    Assert.Equal(0, calls);
}

[Fact]
public void UnknownReqId_IsIgnored_NoThrow()
{
    var w = new IbWrapper();
    w.tickByTickAllLast(999, 1, 1L, 1.0, 1m, new IBApi.TickAttribLast(), "", "");
    w.realtimeBar(999, 1L, 1, 1, 1, 1, 1m, 1m, 1);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter FullyQualifiedName~IbWrapperTests`
Expected: FAIL — `IbTradeUpdate`/`RegisterTickSink` undefined.

- [ ] **Step 3: Create the DTOs**

```csharp
// IbTradeUpdate.cs
namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Raw tick-by-tick "AllLast" values straight off the EReader pump (IB time is Unix seconds; size is decimal).
internal readonly record struct IbTradeUpdate(long TimeSec, double Price, decimal Size);
```

```csharp
// IbRealtimeBar.cs
namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Raw reqRealTimeBars 5s bar values straight off the pump (IB date is Unix seconds; volume is decimal).
internal readonly record struct IbRealtimeBar(long DateSec, double Open, double High, double Low, double Close, decimal Volume);
```

- [ ] **Step 4: Extend `IbWrapper` with the market-data demux**

Add fields + methods + overrides (keep the existing contract-details correlator + `nextValidId` + `error` untouched except where Task 3 extends `error`):

```csharp
private readonly ConcurrentDictionary<int, Action<IbTradeUpdate>> _tickSinks = new();
private readonly ConcurrentDictionary<int, Action<IbRealtimeBar>> _barSinks = new();

public void RegisterTickSink(int reqId, Action<IbTradeUpdate> sink) => _tickSinks[reqId] = sink;
public void RegisterBarSink(int reqId, Action<IbRealtimeBar> sink) => _barSinks[reqId] = sink;
public void ReleaseMarketData(int reqId) { _tickSinks.TryRemove(reqId, out _); _barSinks.TryRemove(reqId, out _); }

public override void tickByTickAllLast(int reqId, int tickType, long time, double price, decimal size,
    TickAttribLast tickAttribLast, string exchange, string specialConditions)
{
    if (_tickSinks.TryGetValue(reqId, out var sink))
        sink(new IbTradeUpdate(time, price, size));
}

public override void realtimeBar(int reqId, long time, double open, double high, double low, double close,
    decimal volume, decimal WAP, int count)
{
    if (_barSinks.TryGetValue(reqId, out var sink))
        sink(new IbRealtimeBar(time, open, high, low, close, volume));
}
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter FullyQualifiedName~IbWrapperTests`
Expected: PASS (all, including the Plan-1 contract-details tests).

- [ ] **Step 6: Commit** *(controller)*

```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbTradeUpdate.cs \
        src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbRealtimeBar.cs \
        src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbWrapper.cs \
        tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers/IbWrapperTests.cs
git commit -m "feat(livehost): IbWrapper tick/bar reqId demux + raw IB market-data DTOs"
```

---

### Task 3: `IbWrapper` reconnect signal + resettable nextValidId + historical-ticks correlator

**Files:**
- Modify: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbWrapper.cs`
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbHistoricalTick.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers/IbWrapperTests.cs` (extend)

**Interfaces:**
- Produces:
  - `internal readonly record struct IbHistoricalTick(long TimeSec, double Price, decimal Size);`
  - `IbWrapper`: `event Action? ConnectionDropped`; `void ResetForReconnect()` (re-arms `NextValidId`); `Task<IReadOnlyList<IbHistoricalTick>> RegisterHistoricalTicks(int reqId)` returns an awaitable completed on `historicalTicksLast(done:true)`; `connectionClosed()` and `error(id==-1, 1100)` raise `ConnectionDropped`.

- [ ] **Step 1: Write the failing tests**

```csharp
// append to IbWrapperTests.cs
[Fact]
public void ConnectionClosed_RaisesConnectionDropped()
{
    var w = new IbWrapper();
    int drops = 0;
    w.ConnectionDropped += () => drops++;
    w.connectionClosed();
    Assert.Equal(1, drops);
}

[Fact]
public void Error1100_RaisesConnectionDropped_ButOtherMinusOneNoticesDoNot()
{
    var w = new IbWrapper();
    int drops = 0;
    w.ConnectionDropped += () => drops++;
    w.error(-1, 0L, 2104, "Market data farm connection is OK", ""); // benign
    w.error(-1, 0L, 1100, "Connectivity between IB and TWS has been lost.", "");
    Assert.Equal(1, drops);
}

[Fact]
public async Task ResetForReconnect_RearmsNextValidId()
{
    var w = new IbWrapper();
    w.nextValidId(1);
    Assert.Equal(1, await w.NextValidId);
    w.ResetForReconnect();
    w.nextValidId(7);
    Assert.Equal(7, await w.NextValidId);
}

[Fact]
public async Task HistoricalTicksLast_AccumulatesAndCompletesOnDone()
{
    var w = new IbWrapper();
    var task = w.RegisterHistoricalTicks(30);
    w.historicalTicksLast(30, new[] { HistTick(1700, 10.0, 2m) }, done: false);
    w.historicalTicksLast(30, new[] { HistTick(1701, 11.0, 3m) }, done: true);

    var result = await task;
    Assert.Equal(2, result.Count);
    Assert.Equal(1700, result[0].TimeSec);
    Assert.Equal(11.0, result[1].Price);
}

private static IBApi.HistoricalTickLast HistTick(long time, double price, decimal size) =>
    new() { Time = time, Price = price, Size = size };
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter FullyQualifiedName~IbWrapperTests`
Expected: FAIL — `ConnectionDropped`/`ResetForReconnect`/`RegisterHistoricalTicks` undefined.

- [ ] **Step 3: Create `IbHistoricalTick`**

```csharp
// IbHistoricalTick.cs
namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// One reqHistoricalTicks "TRADES" row (IB time is Unix seconds; size is decimal).
internal readonly record struct IbHistoricalTick(long TimeSec, double Price, decimal Size);
```

- [ ] **Step 4: Implement the wrapper additions**

Change `_nextValidId` from `readonly` to reassignable, add the drop event + historical correlator, and extend `error`:

```csharp
private TaskCompletionSource<int> _nextValidId = new(TaskCreationOptions.RunContinuationsAsynchronously);
private readonly ConcurrentDictionary<int, (List<IbHistoricalTick> Items, TaskCompletionSource<IReadOnlyList<IbHistoricalTick>> Tcs)> _histByReq = new();

public Task<int> NextValidId => _nextValidId.Task;
public event Action? ConnectionDropped;

// A reconnect issues a fresh nextValidId; re-arm the awaiter so the new value is observed (a completed TCS can't be re-set).
public void ResetForReconnect() => _nextValidId = new(TaskCreationOptions.RunContinuationsAsynchronously);

public Task<IReadOnlyList<IbHistoricalTick>> RegisterHistoricalTicks(int reqId)
{
    var entry = _histByReq.GetOrAdd(reqId, _ => ([], new(TaskCreationOptions.RunContinuationsAsynchronously)));
    return entry.Tcs.Task;
}

public override void connectionClosed() => ConnectionDropped?.Invoke();

public override void historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done)
{
    if (!_histByReq.TryGetValue(reqId, out var entry)) return;
    foreach (var t in ticks) entry.Items.Add(new IbHistoricalTick(t.Time, t.Price, t.Size));
    if (done && _histByReq.TryRemove(reqId, out var finished))
        finished.Tcs.TrySetResult(finished.Items.ToArray());
}
```

Extend the existing `error` override (preserve the Plan-1 `id >= 0` contract-details faulting) by adding the connectivity-lost branch at the top:

```csharp
public override void error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)
{
    if (id == -1 && errorCode == 1100) { ConnectionDropped?.Invoke(); return; }
    if (id >= 0 && _byReq.TryGetValue(id, out var pending))
        pending.Completion.TrySetException(new IbRequestException(errorCode, errorMsg));
}
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter FullyQualifiedName~IbWrapperTests`
Expected: PASS (all, including Plan-1 tests).

- [ ] **Step 6: Commit** *(controller)*

```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbWrapper.cs \
        src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbHistoricalTick.cs \
        tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers/IbWrapperTests.cs
git commit -m "feat(livehost): IbWrapper reconnect signal, resettable nextValidId, historical-ticks correlator"
```

---

### Task 4: `IbConnection` residual (a) — pump Join + reconnect-capable

**Files:**
- Modify: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbConnection.cs`

**Interfaces:**
- Produces: `IbConnection.Disconnect()` now wakes (`issueSignal`) and `Join`s the pump thread with a bounded timeout; `Connect(...)` is safe to call again after a `Disconnect` (reconnect) — a fresh signal/socket/pump per attempt (Plan-1 invariant preserved), and the reader thread + signal are tracked so `Disconnect`/`DisposeAsync` can join the live one.

> **Note on testing:** `IbConnection` wraps a real `EClientSocket`/`EReader` and cannot be meaningfully unit-tested in isolation (the Plan-1 suite covers it only via the gated paper integration). This task is verified by (a) the build, (b) the gated paper integration in Task 15 (now exercising a reconnect), and (c) code review of the Join/teardown logic. No new unit test is added; do not fabricate one against the real socket.

- [ ] **Step 1: Track the live signal + join on teardown**

Store the active `EReaderMonitorSignal` alongside `_readerThread`, and in `StartReaderPump` capture it. Replace `Disconnect()`/`Teardown` so teardown wakes and joins the pump:

```csharp
private EReaderMonitorSignal? _signal;

private void StartReaderPump(EClientSocket client, EReaderMonitorSignal signal)
{
    _signal = signal;
    var reader = new EReader(client, signal);
    reader.Start();
    _readerThread = new Thread(() =>
    {
        while (client.IsConnected())
        {
            signal.waitForSignal();
            reader.processMsgs();
        }
    }) { IsBackground = true, Name = "ib-ereader" };
    _readerThread.Start();
}

public void Disconnect()
{
    var thread = _readerThread;
    if (_client?.IsConnected() == true)
        _client.eDisconnect();          // breaks the while(IsConnected) loop condition
    _signal?.issueSignal();              // wake the pump if it is parked in waitForSignal()
    thread?.Join(TimeSpan.FromSeconds(5)); // bounded: never hang shutdown on a stuck pump
    _readerThread = null;
    _signal = null;
}
```

> `StartReaderPump`'s signature changes to take `EReaderMonitorSignal` (the concrete type) so the field type matches. Update its call site in `Connect`.

- [ ] **Step 2: Make the wrapper re-arm on each Connect attempt**

At the top of each `Connect` attempt (before `eConnect`), re-arm the wrapper's nextValidId so a reconnect's second `nextValidId` is observed:

```csharp
wrapper.ResetForReconnect();
var signal = new EReaderMonitorSignal();
var client = new EClientSocket(wrapper, signal);
```

- [ ] **Step 3: Build**

Run: `dotnet build AlgoTradeForge.slnx`
Expected: build succeeds, 0 warnings/0 errors.

- [ ] **Step 4: Regression — Plan-1 infra suite still green**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/`
Expected: PASS (gated paper tests skip without `IB_PAPER_HOST`).

- [ ] **Step 5: Commit** *(controller)*

```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbConnection.cs
git commit -m "fix(livehost): join IbConnection pump on disconnect + re-arm nextValidId per connect (reconnect-ready)"
```

---

### Task 5: `IIbMarketDataClient` + real socket adapter

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IIbMarketDataClient.cs`
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbConnectionMarketDataClient.cs`

**Interfaces:**
- Produces:
```csharp
internal interface IIbMarketDataClient
{
    Task Connect(CancellationToken ct = default);
    void RequestTrades(int reqId, ResolvedIbContract contract);
    void RequestRealtimeBars(int reqId, ResolvedIbContract contract);
    void CancelTrades(int reqId);
    void CancelRealtimeBars(int reqId);
}
```
This is the fakeable seam `IbSession` (Task 6) is tested against; the real impl issues IBApi calls over `IbConnection.Client`.

> **Testing:** the real adapter is a thin pass-through to `EClientSocket` and is verified by the gated paper integration (Task 15), not a unit test (it needs a real socket). Its consumer `IbSession` is unit-tested in Task 6 via a fake `IIbMarketDataClient`.

- [ ] **Step 1: Create the interface**

```csharp
// IIbMarketDataClient.cs
namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// The market-data request surface over the shared IB socket. Abstracted so IbSession's
// subscribe/reconnect orchestration is unit-testable without a real EClientSocket.
internal interface IIbMarketDataClient
{
    Task Connect(CancellationToken ct = default);
    void RequestTrades(int reqId, ResolvedIbContract contract);
    void RequestRealtimeBars(int reqId, ResolvedIbContract contract);
    void CancelTrades(int reqId);
    void CancelRealtimeBars(int reqId);
}
```

- [ ] **Step 2: Create the real adapter**

```csharp
// IbConnectionMarketDataClient.cs
namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Real IIbMarketDataClient: issues tick-by-tick + 5s realtime-bar requests on the shared socket.
// IBApi vocabulary stops here. tickType "AllLast" + barSize 5 "TRADES" mirror the POC.
internal sealed class IbConnectionMarketDataClient(IbConnection connection) : IIbMarketDataClient
{
    public Task Connect(CancellationToken ct = default) => connection.Connect(ct: ct);

    public void RequestTrades(int reqId, ResolvedIbContract contract) =>
        connection.Client.reqTickByTickData(reqId, contract.ToIbApiContract(), "AllLast", 0, false);

    public void RequestRealtimeBars(int reqId, ResolvedIbContract contract) =>
        connection.Client.reqRealTimeBars(reqId, contract.ToIbApiContract(), 5, "TRADES", false, null);

    public void CancelTrades(int reqId) => connection.Client.cancelTickByTickData(reqId);
    public void CancelRealtimeBars(int reqId) => connection.Client.cancelRealTimeBars(reqId);
}
```

> **Dependency check:** confirm `IbContractTranslation` exposes a `ToIbApiContract` for `ResolvedIbContract` (Plan 1 has it for `IbContract` via `spec.ToIbApiContract()` and resolution carries `ConId`). If only `IbContract` has the extension, add a `ResolvedIbContract` overload here that sets `Contract.ConId = resolved.ConId` (conId alone fully specifies an IB contract for data requests). Implement that overload in `IbContractTranslation.cs` if missing, with a focused test in `IbContractTranslationTests.cs`.

- [ ] **Step 3: Build**

Run: `dotnet build AlgoTradeForge.slnx`
Expected: succeeds.

- [ ] **Step 4: Commit** *(controller)*

```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IIbMarketDataClient.cs \
        src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbConnectionMarketDataClient.cs
git commit -m "feat(livehost): IIbMarketDataClient seam + real socket adapter (tick-by-tick + 5s bars)"
```

---

### Task 6: `IbSession` — shared session, typed subscribe, reconnect → re-subscribe

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbSession.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers/IbSessionTests.cs`

**Interfaces:**
- Consumes: `IIbMarketDataClient`, `IbWrapper`, `ResolvedIbContract`, `IbTradeUpdate`, `IbRealtimeBar`.
- Produces:
```csharp
internal sealed class IbSession : IAsyncDisposable
{
    IbSession(IIbMarketDataClient client, IbWrapper wrapper);                       // ctor starts the reconnect worker
    Task Connect(CancellationToken ct = default);                                   // connect the shared socket
    int SubscribeTrades(ResolvedIbContract contract, Action<IbTradeUpdate> sink);   // returns reqId
    int SubscribeRealtimeBars(ResolvedIbContract contract, Action<IbRealtimeBar> sink);
    void Unsubscribe(int reqId);
    event Action? Reconnected;                                                      // fired AFTER re-subscribe completes
    ValueTask DisposeAsync();                                                       // cancels + awaits the reconnect worker
}
```
**Reconnect is fully async (no sync-over-async — controller pre-flight decision).** `wrapper.ConnectionDropped` (raised on the pump thread) only *signals* a bounded `Channel<bool>`; a long-running worker `Task` owned by `IbSession` drains that channel and, per drop, re-`Connect`s, re-issues every tracked subscription under the same reqIds (re-registering its sink), then raises `Reconnected` so bar sources can trigger catch-up. `DisposeAsync` cancels the worker CTS, completes the channel, and awaits the worker.

- [ ] **Step 1: Write the failing tests (subscribe + reconnect re-subscribe)**

```csharp
// IbSessionTests.cs
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class IbSessionTests
{
    private sealed class FakeClient : IIbMarketDataClient
    {
        public int Connects { get; private set; }
        public List<(int ReqId, string Kind)> Requests { get; } = [];
        public Task Connect(CancellationToken ct = default) { Connects++; return Task.CompletedTask; }
        public void RequestTrades(int reqId, ResolvedIbContract c) => Requests.Add((reqId, "trades"));
        public void RequestRealtimeBars(int reqId, ResolvedIbContract c) => Requests.Add((reqId, "bars"));
        public void CancelTrades(int reqId) { }
        public void CancelRealtimeBars(int reqId) { }
    }

    private static ResolvedIbContract Aapl() =>
        new(new IbContract("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD"), 265598, "AAPL", "");

    [Fact]
    public void SubscribeTrades_IssuesRequest_AndRoutesTicks()
    {
        var client = new FakeClient();
        var wrapper = new IbWrapper();
        var session = new IbSession(client, wrapper);

        IbTradeUpdate? seen = null;
        var reqId = session.SubscribeTrades(Aapl(), u => seen = u);

        Assert.Single(client.Requests);
        Assert.Equal((reqId, "trades"), client.Requests[0]);

        // wrapper callback flows to the sink the session registered
        wrapper.tickByTickAllLast(reqId, 1, 1700L, 1.0, 2m, new IBApi.TickAttribLast(), "", "");
        Assert.NotNull(seen);
    }

    [Fact]
    public async Task ConnectionDropped_Reconnects_AndResubscribesAll_ThenRaisesReconnected()
    {
        var client = new FakeClient();
        var wrapper = new IbWrapper();
        await using var session = new IbSession(client, wrapper);

        session.SubscribeTrades(Aapl(), _ => { });
        session.SubscribeRealtimeBars(Aapl(), _ => { });
        client.Requests.Clear();

        // Reconnect is async (worker task) — await the Reconnected event to observe completion deterministically.
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Reconnected += () => reconnected.TrySetResult();

        wrapper.connectionClosed(); // drop -> signals the worker
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, client.Connects);                 // re-connected
        Assert.Equal(2, client.Requests.Count);           // both re-issued
        Assert.Contains(client.Requests, r => r.Kind == "trades");
        Assert.Contains(client.Requests, r => r.Kind == "bars");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter FullyQualifiedName~IbSessionTests`
Expected: FAIL — `IbSession` undefined.

- [ ] **Step 3: Implement `IbSession`**

```csharp
// IbSession.cs
using System.Threading.Channels;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// The shared per-venue IB session: owns reqId allocation over the one socket and tracks every active
// market-data subscription so a reconnect can re-issue them. The data plane (Plan 3) and the order
// plane (Plan 4) both hold a handle to this. Reconnect is FULLY ASYNC: IbWrapper.ConnectionDropped
// (raised on the EReader pump thread) only signals a bounded channel; a worker task drains it and
// reconnects off the pump thread (no sync-over-async, no blocking the dying pump). The subscription
// map is guarded by a lock (Subscribe runs on the caller thread; the worker re-issues under the lock).
internal sealed class IbSession : IAsyncDisposable
{
    private abstract record Sub(int ReqId, ResolvedIbContract Contract);
    private sealed record TradeSub(int ReqId, ResolvedIbContract Contract, Action<IbTradeUpdate> Sink) : Sub(ReqId, Contract);
    private sealed record BarSub(int ReqId, ResolvedIbContract Contract, Action<IbRealtimeBar> Sink) : Sub(ReqId, Contract);

    private readonly IIbMarketDataClient _client;
    private readonly IbWrapper _wrapper;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Sub> _subs = new();
    private readonly Channel<bool> _drops = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite }); // coalesce bursts to one pending reconnect
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private int _nextReqId;

    public event Action? Reconnected;

    public IbSession(IIbMarketDataClient client, IbWrapper wrapper)
    {
        _client = client;
        _wrapper = wrapper;
        _wrapper.ConnectionDropped += OnConnectionDropped;
        _worker = Task.Run(ReconnectLoop);
    }

    public Task Connect(CancellationToken ct = default) => _client.Connect(ct);

    public int SubscribeTrades(ResolvedIbContract contract, Action<IbTradeUpdate> sink)
    {
        var reqId = Interlocked.Increment(ref _nextReqId);
        using (_gate.EnterScope()) _subs[reqId] = new TradeSub(reqId, contract, sink);
        _wrapper.RegisterTickSink(reqId, sink);
        _client.RequestTrades(reqId, contract);
        return reqId;
    }

    public int SubscribeRealtimeBars(ResolvedIbContract contract, Action<IbRealtimeBar> sink)
    {
        var reqId = Interlocked.Increment(ref _nextReqId);
        using (_gate.EnterScope()) _subs[reqId] = new BarSub(reqId, contract, sink);
        _wrapper.RegisterBarSink(reqId, sink);
        _client.RequestRealtimeBars(reqId, contract);
        return reqId;
    }

    public void Unsubscribe(int reqId)
    {
        Sub? sub;
        using (_gate.EnterScope()) { _subs.Remove(reqId, out sub); }
        if (sub is null) return;
        _wrapper.ReleaseMarketData(reqId);
        switch (sub) { case TradeSub: _client.CancelTrades(reqId); break; case BarSub: _client.CancelRealtimeBars(reqId); break; }
    }

    // Pump-thread callback — do NOT block here. Just signal the worker (coalesced to one pending reconnect).
    private void OnConnectionDropped() => _drops.Writer.TryWrite(true);

    private async Task ReconnectLoop()
    {
        try
        {
            await foreach (var _ in _drops.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                await Reconnect(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { /* shutdown */ }
    }

    private async Task Reconnect(CancellationToken ct)
    {
        _wrapper.ResetForReconnect();
        await _client.Connect(ct).ConfigureAwait(false);

        List<Sub> active;
        using (_gate.EnterScope()) active = [.. _subs.Values];
        foreach (var sub in active)
        {
            switch (sub)
            {
                case TradeSub t: _wrapper.RegisterTickSink(t.ReqId, t.Sink); _client.RequestTrades(t.ReqId, t.Contract); break;
                case BarSub b: _wrapper.RegisterBarSink(b.ReqId, b.Sink); _client.RequestRealtimeBars(b.ReqId, b.Contract); break;
            }
        }
        Reconnected?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _wrapper.ConnectionDropped -= OnConnectionDropped;
        _drops.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}
```

> **Concurrency note for the reviewer:** `OnConnectionDropped` runs on the EReader pump thread and only does a non-blocking `TryWrite` — it never blocks the pump (controller pre-flight decision: no sync-over-async). The worker `Task` performs `await Connect()` + re-subscribe off the pump thread. The single-slot `DropWrite` channel coalesces a burst of drop signals into at most one pending reconnect. Re-issuing under the SAME reqIds is intentional — the new socket has no prior request state, and reusing ids keeps the sink registrations valid. `Reconnected` fires on the worker thread; subscribers (bar sources) must treat it as a cross-thread callback.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter FullyQualifiedName~IbSessionTests`
Expected: PASS (both).

- [ ] **Step 5: Commit** *(controller)*

```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbSession.cs \
        tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers/IbSessionTests.cs
git commit -m "feat(livehost): IbSession shared session with typed subscribe + reconnect re-subscribe"
```

---

### Task 7: `IbVenueConnector : IVenueConnector` (tick lane)

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbDataPlaneOptions.cs`
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbVenueConnector.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers/IbVenueConnectorTests.cs`

**Interfaces:**
- Consumes: `IbSession`, `IIbContractResolver`, `IbContractMapping`, `IbTradeUpdate`, `TickScale`, `IVenueConnector`, `TradeEvent`.
- Produces:
  - `IbDataPlaneOptions` (record/class): `Dictionary<string, TickScale> InstrumentScales`, `TickScale DefaultScale`, `int IngestChannelCapacity`, `long MaxGapMs`, plus connection fields (host/port/clientId) — but connection fields may live on `IbConnectionOptions`; keep scales/capacity/gap here.
  - `IbVenueConnector : IVenueConnector` with `Venue => "ib"`, `SessionPolicy => SingleSession`. Requires an instrument→`Asset` resolver to map names to contracts (inject `IAssetRepository` or a `Func<string, Asset>`; the host already registers `IAssetRepository`).

- [ ] **Step 1: Create `IbDataPlaneOptions`**

```csharp
// IbDataPlaneOptions.cs
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance; // TickScale

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

public sealed class IbDataPlaneOptions
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 4004;
    public int ClientId { get; init; } = 11;
    public int IngestChannelCapacity { get; init; } = 4096;
    public long MaxGapMs { get; init; } = 30_000; // time-gate disconnect threshold; tune per instrument liquidity
    public Dictionary<string, TickScale> InstrumentScales { get; init; } = new();
    public TickScale DefaultScale { get; init; } = new(PriceExp: 2, QtyExp: 0); // equities: cent ticks, whole shares
}
```

> Reuse `AlgoTradeForge.LiveHost.Infrastructure.Live.Binance.TickScale` (already `public`) rather than defining a second scale type — it is venue-neutral (independent price/qty exponents).

- [ ] **Step 2: Write the failing test**

```csharp
// IbVenueConnectorTests.cs
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Assets;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class IbVenueConnectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Identity_IsIbSingleSession()
    {
        var c = BuildConnector(out _, out _);
        Assert.Equal("ib", c.Venue);
        Assert.Equal(MarketDataSessionPolicy.SingleSession, c.SessionPolicy);
    }

    [Fact]
    public void InstrumentScale_UsesConfiguredThenDefault()
    {
        var opts = new IbDataPlaneOptions
        {
            InstrumentScales = { ["AAPL"] = new TickScale(2, 0) },
            DefaultScale = new TickScale(4, 1),
        };
        var c = BuildConnector(out _, out _, opts);
        Assert.Equal(((sbyte)2, (sbyte)0), c.InstrumentScale("AAPL"));
        Assert.Equal(((sbyte)4, (sbyte)1), c.InstrumentScale("UNKNOWN"));
    }

    [Fact]
    public async Task Stream_MapsTickUpdate_ToScaledTradeEvent()
    {
        var c = BuildConnector(out var session, out _,
            new IbDataPlaneOptions { InstrumentScales = { ["AAPL"] = new TickScale(2, 0) } });

        // Drive one tick through the session sink the connector registers, then assert the yielded event.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var events = new List<IMarketEvent>();
        var pump = Task.Run(async () =>
        {
            await foreach (var ev in c.Stream(["AAPL"], cts.Token)) { events.Add(ev); break; }
        }, Ct);

        await session.WaitForSubscription(Ct);                 // fake session exposes the registered sink
        session.PushTrade("AAPL", new IbTradeUpdate(1_700_000_000L, 296.98, 3m));
        await pump;

        var te = Assert.IsType<TradeEvent>(events[0]);
        Assert.Equal("AAPL", te.Instrument);
        Assert.Equal(1_700_000_000_000L, te.Tick.TimestampMs); // seconds → ms
        Assert.Equal(29_698L, te.Tick.Price);                  // 296.98 × 10^2
        Assert.Equal(3L, te.Tick.Quantity);                    // 3 × 10^0
        Assert.Equal(AggressorSide.Unknown, te.Tick.Aggressor);
    }

    // BuildConnector wires a fake IbSession seam + a contract resolver returning AAPL conId.
    private static IbVenueConnector BuildConnector(out FakeIbSession session, out IIbContractResolver resolver,
        IbDataPlaneOptions? opts = null) { /* see Step 4 for FakeIbSession; resolver via Substitute */ ... }
}
```

> **Design constraint surfaced by the test:** `IbVenueConnector` must be testable without a real socket. Introduce a thin seam the connector subscribes through — reuse `IbSession` but inject the `IIbMarketDataClient` + `IbWrapper` it composes via a fake (preferred), OR extract an `IIbMarketDataSession` interface over `IbSession`'s `SubscribeTrades`/`Connect` and have the connector depend on that. **Pick the interface extraction**: define `internal interface IIbMarketDataSession { Task Connect(CancellationToken ct=default); int SubscribeTrades(ResolvedIbContract c, Action<IbTradeUpdate> sink); int SubscribeRealtimeBars(ResolvedIbContract c, Action<IbRealtimeBar> sink); void Unsubscribe(int reqId); event Action? Reconnected; }` and make `IbSession` implement it. The connector + bar source depend on `IIbMarketDataSession`; tests use a fake.

- [ ] **Step 3: Extract `IIbMarketDataSession` and make `IbSession` implement it**

Create `IIbMarketDataSession.cs` with the interface above; add `: IIbMarketDataSession` to `IbSession` (its members already match). No behavior change.

- [ ] **Step 4: Implement `IbVenueConnector`**

```csharp
// IbVenueConnector.cs
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// IB tick lane: resolves each instrument's contract, subscribes tick-by-tick on the shared session,
// and bridges the EWrapper push callbacks through a bounded channel into the pull IVenueConnector seam.
// Independent price/qty scaling via configured TickScale exponents (mirrors BinanceVenueConnector).
internal sealed class IbVenueConnector(
    IIbMarketDataSession session,
    IIbContractResolver resolver,
    Func<string, AlgoTradeForge.Domain.Asset> assetFor,
    IbDataPlaneOptions options) : IVenueConnector
{
    public string Venue => "ib";
    public MarketDataSessionPolicy SessionPolicy => MarketDataSessionPolicy.SingleSession;

    public (sbyte PriceScaleExp, sbyte QtyScaleExp) InstrumentScale(string instrument)
    {
        var s = ScaleFor(instrument);
        return ((sbyte)s.PriceExp, (sbyte)s.QtyExp);
    }

    public async IAsyncEnumerable<IMarketEvent> Stream(
        IReadOnlyList<string> instruments, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateBounded<IMarketEvent>(
            new BoundedChannelOptions(options.IngestChannelCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });

        await session.Connect(ct).ConfigureAwait(false);

        foreach (var instrument in instruments)
        {
            var scale = ScaleFor(instrument);
            var seq = new SyntheticSequence();
            var resolved = await resolver.Resolve(assetFor(instrument).ToIbContract(), ct).ConfigureAwait(false);
            session.SubscribeTrades(resolved, update =>
                channel.Writer.TryWrite(ToTradeEvent(instrument, update, scale, seq.Next())));
        }

        await foreach (var ev in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return ev;
    }

    internal static TradeEvent ToTradeEvent(string instrument, IbTradeUpdate u, TickScale scale, long sequence)
    {
        var tick = new TradeTick(
            TimestampMs: u.TimeSec * 1000,
            Price: scale.ScalePrice((decimal)u.Price),
            Quantity: scale.ScaleQty(u.Size),
            Sequence: sequence,
            Aggressor: AggressorSide.Unknown);
        return new TradeEvent(instrument, tick);
    }

    private TickScale ScaleFor(string instrument) =>
        options.InstrumentScales.TryGetValue(instrument, out var s) ? s : options.DefaultScale;

    // Per-instrument monotonic archive sequence (IB carries none). Single-writer per instrument (the pump thread).
    private sealed class SyntheticSequence { private long _n; public long Next() => Interlocked.Increment(ref _n); }
}
```

> Add `using AlgoTradeForge.Domain.History;` for `TradeTick`/`AggressorSide`. `assetFor` is supplied by the host from `IAssetRepository`; in tests it is a lambda returning a fixed `EquityAsset`.

- [ ] **Step 5: Finish `FakeIbSession` in the test and run**

Implement `FakeIbSession : IIbMarketDataSession` capturing the registered trade sink keyed by instrument, with `WaitForSubscription`/`PushTrade(instrument, update)` helpers (a `TaskCompletionSource` set when `SubscribeTrades` is first called). Then:

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter FullyQualifiedName~IbVenueConnectorTests`
Expected: PASS (all three).

- [ ] **Step 6: Commit** *(controller)*

```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbDataPlaneOptions.cs \
        src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IIbMarketDataSession.cs \
        src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbSession.cs \
        src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbVenueConnector.cs \
        tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers/IbVenueConnectorTests.cs
git commit -m "feat(livehost): IbVenueConnector tick lane (push->channel->pull bridge, configured scaling)"
```

---

### Task 8: `IbVenueBarSource : IBarSource` (5s venue-bar lane)

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbVenueBarSource.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers/IbVenueBarSourceTests.cs`

**Interfaces:**
- Consumes: `IIbMarketDataSession`, `ResolvedIbContract`, `IbRealtimeBar`, `IBarSource`, `ScaleContext`, `Int64Bar`.
- Produces: `internal sealed class IbVenueBarSource : IBarSource` — `Start()` subscribes realtime bars; each `realtimeBar` → scaled `Int64Bar` → `onBar(bar, isStart:false)`; maintains a bounded `Recent`.

- [ ] **Step 1: Write the failing test**

```csharp
// IbVenueBarSourceTests.cs
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class IbVenueBarSourceTests
{
    private static ResolvedIbContract Aapl() =>
        new(new IbContract("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD"), 265598, "AAPL", "");

    [Fact]
    public async Task RealtimeBar_ScalesAndEmits_AndRecords()
    {
        var session = new FakeIbSession();                          // reused from Task 7
        var scale = new ScaleContext(tickSize: 0.01m);
        Int64Bar? emitted = null;
        var src = new IbVenueBarSource(session, Aapl(), scale, (bar, _) => emitted = bar);

        await src.Start();
        session.PushBar(Aapl().ConId, new IbRealtimeBar(1_700_000_005L, 1.00, 2.00, 0.50, 1.50, 10m));

        Assert.NotNull(emitted);
        Assert.Equal(1_700_000_005_000L, emitted!.Value.OpenTime); // seconds → ms
        Assert.Equal(scale.FromMarketPrice(2.00m), emitted.Value.High);
        Assert.Equal(10L, emitted.Value.Volume);
        Assert.Single(src.Recent);
    }
}
```

> Extend `FakeIbSession` (Task 7) with `SubscribeRealtimeBars` capture + `PushBar(conId, IbRealtimeBar)`.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter FullyQualifiedName~IbVenueBarSourceTests`
Expected: FAIL — `IbVenueBarSource` undefined.

- [ ] **Step 3: Implement `IbVenueBarSource`**

```csharp
// IbVenueBarSource.cs
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// IB venue-published 5s bar lane (reqRealTimeBars "TRADES"). Mirrors KlineVenueBarSource: subscribes
// on Start, scales each bar via the asset ScaleContext, emits via onBar, and keeps a bounded Recent.
internal sealed class IbVenueBarSource(
    IIbMarketDataSession session, ResolvedIbContract contract, ScaleContext scale,
    Action<Int64Bar, bool> onBar, int recentCapacity = 256) : IBarSource
{
    private readonly Queue<Int64Bar> _recent = new(recentCapacity);
    private readonly Lock _gate = new();

    public IReadOnlyList<Int64Bar> Recent { get { using (_gate.EnterScope()) return _recent.ToArray(); } }

    public Task Start()
    {
        session.SubscribeRealtimeBars(contract, OnBar);
        return Task.CompletedTask;
    }

    private void OnBar(IbRealtimeBar b)
    {
        var bar = new Int64Bar(
            b.DateSec * 1000,
            scale.FromMarketPrice((decimal)b.Open),
            scale.FromMarketPrice((decimal)b.High),
            scale.FromMarketPrice((decimal)b.Low),
            scale.FromMarketPrice((decimal)b.Close),
            MoneyConvert.ToLong(b.Volume));
        using (_gate.EnterScope())
        {
            if (_recent.Count >= recentCapacity) _recent.Dequeue();
            _recent.Enqueue(bar);
        }
        onBar(bar, false);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter FullyQualifiedName~IbVenueBarSourceTests`
Expected: PASS.

- [ ] **Step 5: Commit** *(controller)*

```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbVenueBarSource.cs \
        tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers/IbVenueBarSourceTests.cs
git commit -m "feat(livehost): IbVenueBarSource 5s venue-published bar lane"
```

---

### Task 9: `IIbHistoricalTicksClient` + real paged `reqHistoricalTicks` adapter

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IIbHistoricalTicksClient.cs`
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbConnectionHistoricalTicksClient.cs`

**Interfaces:**
- Produces:
```csharp
internal interface IIbHistoricalTicksClient
{
    Task<IReadOnlyList<IbHistoricalTick>> FetchTrades(ResolvedIbContract contract, long fromMs, long toMs, CancellationToken ct = default);
}
```
Real impl pages `reqHistoricalTicks` (≤1000/req, `whatToShow:"TRADES"`, `useRth:0`, `ignoreSize:false`) over `IbConnection.Client`, correlating results via `IbWrapper.RegisterHistoricalTicks`, advancing the window until `toMs` is covered or a page returns empty. Respects IB historical pacing by issuing sequentially.

> **Testing:** the real adapter needs a socket + entitlement → verified by gated paper integration (Task 15). Its consumer `IbBackfillRequester` is unit-tested in Task 10 via a fake `IIbHistoricalTicksClient`.

- [ ] **Step 1: Create the interface**

```csharp
// IIbHistoricalTicksClient.cs
namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Fetches historical "TRADES" ticks for [fromMs, toMs] via reqHistoricalTicks. Abstracted so the
// backfill requester is unit-testable without a socket or market-data entitlement.
internal interface IIbHistoricalTicksClient
{
    Task<IReadOnlyList<IbHistoricalTick>> FetchTrades(
        ResolvedIbContract contract, long fromMs, long toMs, CancellationToken ct = default);
}
```

- [ ] **Step 2: Create the real adapter**

```csharp
// IbConnectionHistoricalTicksClient.cs
using System.Globalization;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Real IIbHistoricalTicksClient: pages reqHistoricalTicks forward from `fromMs` until `toMs` is covered.
// IB returns <=1000 ticks/request ascending when startDateTime is given; we advance start past the last
// returned tick each page. IBApi vocabulary stops here.
internal sealed class IbConnectionHistoricalTicksClient(
    IbConnection connection, IbWrapper wrapper) : IIbHistoricalTicksClient
{
    private const int PageSize = 1000;
    private int _reqId;

    public async Task<IReadOnlyList<IbHistoricalTick>> FetchTrades(
        ResolvedIbContract contract, long fromMs, long toMs, CancellationToken ct = default)
    {
        var all = new List<IbHistoricalTick>();
        var cursorMs = fromMs;
        while (cursorMs < toMs)
        {
            ct.ThrowIfCancellationRequested();
            var reqId = Interlocked.Increment(ref _reqId);
            var pending = wrapper.RegisterHistoricalTicks(reqId);
            connection.Client.reqHistoricalTicks(
                reqId, contract.ToIbApiContract(),
                startDateTime: FormatIb(cursorMs), endDateTime: "",
                numberOfTicks: PageSize, whatToShow: "TRADES", useRth: 0, ignoreSize: false, miscOptions: null);

            var page = await pending.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            if (page.Count == 0) break;

            foreach (var t in page) if (t.TimeSec * 1000 < toMs) all.Add(t);
            var lastMs = page[^1].TimeSec * 1000;
            if (lastMs <= cursorMs) break;       // no forward progress — avoid infinite loop
            cursorMs = lastMs + 1;
            if (page.Count < PageSize) break;     // last page
        }
        return all;
    }

    // IB historical start/end format: "yyyyMMdd-HH:mm:ss" in UTC.
    private static string FormatIb(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyyMMdd-HH:mm:ss", CultureInfo.InvariantCulture);
}
```

> Verify the exact `reqHistoricalTicks` `startDateTime` format string against the vendored `EClient`/docs during implementation; IB accepts `"yyyyMMdd-HH:mm:ss"` (UTC) or `"yyyyMMdd HH:mm:ss"` depending on server version. Adjust if the gated integration rejects it.

- [ ] **Step 3: Build**

Run: `dotnet build AlgoTradeForge.slnx`
Expected: succeeds.

- [ ] **Step 4: Commit** *(controller)*

```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IIbHistoricalTicksClient.cs \
        src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbConnectionHistoricalTicksClient.cs
git commit -m "feat(livehost): IIbHistoricalTicksClient + paged reqHistoricalTicks adapter"
```

---

### Task 10: `IbBackfillRequester : IBackfillRequester` (real archival backfill — option C headline)

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbBackfillRequester.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers/IbBackfillRequesterTests.cs`

**Interfaces:**
- Consumes: `IBackfillRequester`, `IIbHistoricalTicksClient`, `IFileStorage`, `ReplayRequest`, `Discontinuity`, `RecoveryPolicy`, `TickScale`, `SegmentWriter<TradeTick>`, `SegmentHeader`, `RelayArchiveReplaySource` (test).
- Produces: `internal sealed class IbBackfillRequester(IIbHistoricalTicksClient client, IFileStorage storage, string relayKeyPrefix, IbDataPlaneOptions options, TimeProvider time) : IBackfillRequester`. `TryBackfill` fetches `[gap.FromTs, gap.ToTs]`, writes a `.atft` segment under `{relayKeyPrefix}/ib/{instrument}/trades/{createdAtMs:D13}-{firstSequence:D19}.atft`, returns `true` when ≥1 tick was archived (replay can now re-read the bridge).

- [ ] **Step 1: Write the failing test (fetch → archive → replay re-reads)**

```csharp
// IbBackfillRequesterTests.cs
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Assets;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class IbBackfillRequesterTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"IbBf_{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class FakeHist(IReadOnlyList<IbHistoricalTick> ticks) : IIbHistoricalTicksClient
    {
        public Task<IReadOnlyList<IbHistoricalTick>> FetchTrades(ResolvedIbContract c, long fromMs, long toMs, CancellationToken ct)
            => Task.FromResult(ticks);
    }

    private static EquityAsset Aapl() => /* construct the AAPL EquityAsset via its factory */ ...;

    [Fact]
    public async Task TryBackfill_FetchesArchivesGap_AndReplaySourceReadsIt()
    {
        Directory.CreateDirectory(_root);
        var storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _root });
        var opts = new IbDataPlaneOptions { InstrumentScales = { ["AAPL"] = new TickScale(2, 0) } };
        var hist = new FakeHist([ new IbHistoricalTick(1700, 296.50, 1m), new IbHistoricalTick(1701, 296.60, 2m) ]);
        var sut = new IbBackfillRequester(hist, storage, "live-md", opts, new FakeTimeProvider());

        var req = new ReplayRequest(Aapl(), "ib", "ticks", FromTs: 0);
        var gap = new Discontinuity(FromTs: 1_700_000L, ToTs: 1_703_000L, DiscontinuityReason.MissingArchive);
        var policy = new RecoveryPolicy(BackfillBudget: TimeSpan.FromSeconds(5), PollInterval: TimeSpan.FromMilliseconds(50));

        var covered = await sut.TryBackfill(req, gap, policy, Ct);
        Assert.True(covered);

        // RelayArchiveReplaySource re-reads the archived bridge ticks.
        var replay = new RelayArchiveReplaySource(storage, "live-md");
        var read = new List<TradeTick>();
        await foreach (var t in replay.Replay(req with { FromTs = 0 }, Ct)) read.Add(t);

        Assert.Equal(2, read.Count);
        Assert.Equal(1_700_000L, read[0].TimestampMs);  // 1700 s × 1000
        Assert.Equal(29_650L, read[0].Price);           // 296.50 × 10^2
    }

    [Fact]
    public async Task TryBackfill_ZeroBudget_ShortCircuitsFalse()
    {
        var storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _root });
        var sut = new IbBackfillRequester(new FakeHist([]), storage, "live-md", new IbDataPlaneOptions(), new FakeTimeProvider());
        var covered = await sut.TryBackfill(
            new ReplayRequest(Aapl(), "ib", "ticks", 0),
            new Discontinuity(1, 2, DiscontinuityReason.MissingArchive),
            new RecoveryPolicy(TimeSpan.Zero, TimeSpan.FromSeconds(1)), Ct);
        Assert.False(covered);
    }
}
```

> Construct the AAPL `EquityAsset` via the same factory the Plan-1 mapping tests use (`IbContractMappingTests.cs` shows it) so `req.Asset.Name == "AAPL"` and `AssetDirectoryName.From` resolves the instrument key the replay source expects.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter FullyQualifiedName~IbBackfillRequesterTests`
Expected: FAIL — `IbBackfillRequester` undefined.

- [ ] **Step 3: Implement `IbBackfillRequester`**

```csharp
// IbBackfillRequester.cs
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using AlgoTradeForge.Storage;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// IB gap policy (option C): fetch the gap window from reqHistoricalTicks and write it as a relay .atft
// segment so the replay source can re-read it contiguously. Returns true iff >=1 bridge tick was archived.
// Zero budget short-circuits false (parity with the Binance requester's contract).
internal sealed class IbBackfillRequester(
    IIbHistoricalTicksClient client, IFileStorage storage, string relayKeyPrefix,
    IbDataPlaneOptions options, TimeProvider time) : IBackfillRequester
{
    public async Task<bool> TryBackfill(ReplayRequest context, Discontinuity gap, RecoveryPolicy policy, CancellationToken ct = default)
    {
        if (policy.BackfillBudget <= TimeSpan.Zero) return false;

        var instrument = context.Asset.Name;
        var scale = options.InstrumentScales.TryGetValue(instrument, out var s) ? s : options.DefaultScale;
        var resolved = ResolveFor(context); // see note: resolver passed via ctor in host wiring; for the gap window conId is required

        var ticks = await client.FetchTrades(resolved, gap.FromTs, gap.ToTs, ct).ConfigureAwait(false);
        if (ticks.Count == 0) return false;

        var createdAtMs = time.GetUtcNow().ToUnixTimeMilliseconds();
        var firstSeq = 0L; // backfilled ticks are time-ordered; the time gate dedupes by timestamp, not sequence
        var header = new SegmentHeader(
            (sbyte)scale.PriceExp, (sbyte)scale.QtyExp, EpochBaseMs: 0, createdAtMs, firstSeq, TradeTick.PayloadSize);

        using var ms = new MemoryStream();
        using (var writer = new SegmentWriter<TradeTick>(ms, in header, leaveOpen: true))
            foreach (var t in ticks)
                writer.Write(new TradeTick(t.TimeSec * 1000, scale.ScalePrice((decimal)t.Price), scale.ScaleQty(t.Size), 0, AggressorSide.Unknown));

        var name = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{createdAtMs:D13}-{firstSeq:D19}.atft");
        var key = $"{relayKeyPrefix}/ib/{instrument}/trades/{name}";
        await storage.WriteAllBytes(key, ms.ToArray(), ct).ConfigureAwait(false);
        return true;
    }
}
```

> **Resolver wiring decision:** `IbBackfillRequester` needs the `ResolvedIbContract` for the instrument. Two clean options — (a) inject `IIbContractResolver` + `Func<string, Asset> assetFor` and resolve inside `TryBackfill` (consistent with the connector); or (b) inject a `Func<ReplayRequest, ResolvedIbContract>` the host builds from the resolver cache. **Pick (a)**: add `IIbContractResolver resolver, Func<string, Asset> assetFor` to the ctor and replace the `ResolveFor` placeholder with `await resolver.Resolve(assetFor(instrument).ToIbContract(), ct)`. Update the test to pass a `Substitute.For<IIbContractResolver>()` returning the AAPL `ResolvedIbContract` and a lambda `_ => Aapl()`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter FullyQualifiedName~IbBackfillRequesterTests`
Expected: PASS (both).

- [ ] **Step 5: Commit** *(controller)*

```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbBackfillRequester.cs \
        tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers/IbBackfillRequesterTests.cs
git commit -m "feat(livehost): IbBackfillRequester archives reqHistoricalTicks gap into relay .atft (lossless bridge)"
```

---

### Task 11: `IbBarSourceResolver : IBarSourceResolver`

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbBarSourceResolver.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers/IbBarSourceResolverTests.cs`

**Interfaces:**
- Consumes: `IBarSourceResolver`, `IIbMarketDataSession`, `IIbContractResolver`, `Func<string,Asset>`, `IReplaySource`, `IBackfillRequester`, `IInt64BarLoader`, `IbDataPlaneOptions`, `TimeWatermarkGate`, `TickAggregationBarSource`, `IbVenueBarSource`, subscription kinds.
- Produces: `internal sealed class IbBarSourceResolver(...) : IBarSourceResolver` — `TimeBarSubscription → IbVenueBarSource`; `AltBarSubscription → TickAggregationBarSource` with IB catch-up (`gate: new TimeWatermarkGate(options.MaxGapMs)`, replay `RelayArchiveReplaySource(venue:"ib")`, backfill `IbBackfillRequester`); `TickSubscription → null`; Renko → catch-up-fenced (no `catchup:`).

- [ ] **Step 1: Write the failing tests**

```csharp
// IbBarSourceResolverTests.cs (focused on dispatch shape; deep alt-bar behavior is covered by Task 1 + catch-up suite)
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class IbBarSourceResolverTests
{
    private IbBarSourceResolver Build() => /* wire fakes: FakeIbSession, Substitute IIbContractResolver, etc. */ ...;

    [Fact]
    public void TimeBar_ResolvesToVenueBarSource()
    {
        var r = Build();
        var src = r.Resolve("AAPL", new TimeBarSubscription("AAPL", TimeFrame.FromCode("5s")),
            new ScaleContext(0.01m), (_, _) => { });
        Assert.IsType<IbVenueBarSource>(src);
    }

    [Fact]
    public void Tick_ResolvesToNull()
    {
        var r = Build();
        Assert.Null(r.Resolve("AAPL", new TickSubscription("AAPL"), new ScaleContext(0.01m), (_, _) => { }));
    }

    [Fact]
    public void AltBar_ResolvesToTickAggregation()
    {
        var r = Build();
        var src = r.Resolve("AAPL", new AltBarSubscription("AAPL", "EqV-1000000"),
            new ScaleContext(0.01m), (_, _) => { });
        Assert.IsType<TickAggregationBarSource>(src);
    }
}
```

> Confirm the exact `TimeBarSubscription`/`AltBarSubscription`/`TickSubscription` constructors + `TimeFrame.FromCode` against `AlgoTradeForge.Domain.Strategy.Subscriptions` (mirror `BarSourceResolver`'s usage). IB realtime bars are 5s only — the resolver may assert the timeframe is 5s and throw `NotSupportedException` otherwise (mirror Binance's `NotSupportedException` for unknown kinds).

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter FullyQualifiedName~IbBarSourceResolverTests`
Expected: FAIL — `IbBarSourceResolver` undefined.

- [ ] **Step 3: Implement `IbBarSourceResolver`** (mirror `BarSourceResolver`, swap venue specifics)

```csharp
// IbBarSourceResolver.cs
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Infrastructure.History;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

internal sealed class IbBarSourceResolver(
    IIbMarketDataSession session,
    IIbContractResolver contractResolver,
    Func<string, Asset> assetFor,
    IReplaySource replaySource,
    IBackfillRequester backfill,
    IInt64BarLoader warmupLoader,
    IbDataPlaneOptions options,
    CatchupOptions catchupOptions) : IBarSourceResolver
{
    public IBarSource? Resolve(string instrument, DataFeedSubscription subscription, ScaleContext scale, Action<Int64Bar, bool> onBar)
    {
        return subscription switch
        {
            TimeBarSubscription => BuildVenueBar(instrument, scale, onBar),
            AltBarSubscription ab => ResolveAltBar(instrument, ab, scale, onBar),
            TickSubscription => null,
            _ => throw new NotSupportedException($"No IB live bar source for '{subscription.GetType().Name}'."),
        };
    }

    private IbVenueBarSource BuildVenueBar(string instrument, ScaleContext scale, Action<Int64Bar, bool> onBar)
    {
        // Resolution is async; the resolver caches, so block once here (cold path, startup).
        var resolved = contractResolver.Resolve(assetFor(instrument).ToIbContract()).GetAwaiter().GetResult();
        return new IbVenueBarSource(session, resolved, scale, onBar);
    }

    private TickAggregationBarSource ResolveAltBar(string instrument, AltBarSubscription ab, ScaleContext scale, Action<Int64Bar, bool> onBar)
    {
        var feedId = AltBarFeedId.Parse(ab.FeedId);
        var frozen = ThresholdResolver.ResolveParsed(feedId.TypeCode, feedId.Threshold, scale);

        if (feedId.TypeCode == "Renko")    // catch-up fenced (path-dependent _pendingVolume) — same as Binance
            return new TickAggregationBarSource(feedId.TypeCode, frozen, scale, onBar);

        var asset = assetFor(instrument);
        var assetDir = AssetDirectoryName.From(asset);
        var policy = new RecoveryPolicy(catchupOptions.BackfillBudget, catchupOptions.PollInterval);
        var coordinator = new CatchupCoordinator(replaySource, backfill, policy);
        var request = new ReplayRequest(asset, "ib", SourceFeedId: "ticks", FromTs: 0);
        var altBarFeed = new DataFeedDescriptor(catchupOptions.DataRoot, "ib", assetDir, ab.FeedId, DataFeedKind.AltBar);
        var plan = new CatchupPlan(coordinator, request, warmupLoader, altBarFeed, catchupOptions.WarmupBarCount);

        return new TickAggregationBarSource(
            feedId.TypeCode, frozen, scale, onBar, catchup: plan, gate: new TimeWatermarkGate(options.MaxGapMs));
    }
}
```

> Confirm `AltBarFeedId`, `ThresholdResolver`, `DataFeedDescriptor`, `DataFeedKind`, `AssetDirectoryName` namespaces against `BarSourceResolver.cs` (they are identical imports).

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter FullyQualifiedName~IbBarSourceResolverTests`
Expected: PASS.

- [ ] **Step 5: Commit** *(controller)*

```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbBarSourceResolver.cs \
        tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers/IbBarSourceResolverTests.cs
git commit -m "feat(livehost): IbBarSourceResolver (venue 5s bars, IB catch-up alt bars, time gate, Renko fenced)"
```

---

### Task 12: Venue selection + host wiring

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/VenueKind.cs`
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/VenueSelector.cs`
- Modify: `src/AlgoTradeForge.LiveHost.WebApi/Program.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/VenueSelectorTests.cs`

**Interfaces:**
- Produces: `public enum VenueKind { Binance, Ib }`; `public static class VenueSelector { static VenueKind Parse(string? venue); }` (`"ib"`→Ib, `"binance"`/null/default→Binance, unknown→throw). Program.cs branches on it to register the IB trio (`IbSession` singleton, `IbVenueConnector` as `IVenueConnector`, `IbBarSourceResolver` as `IBarSourceResolver`, `IbBackfillRequester` as `IBackfillRequester`, `RelayArchiveReplaySource` with no change) vs the Binance trio (unchanged).

- [ ] **Step 1: Write the failing test**

```csharp
// VenueSelectorTests.cs
using AlgoTradeForge.LiveHost.Infrastructure.Live;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public class VenueSelectorTests
{
    [Theory]
    [InlineData("ib", VenueKind.Ib)]
    [InlineData("IB", VenueKind.Ib)]
    [InlineData("binance", VenueKind.Binance)]
    [InlineData(null, VenueKind.Binance)]
    [InlineData("", VenueKind.Binance)]
    public void Parse_MapsKnownVenues(string? input, VenueKind expected) =>
        Assert.Equal(expected, VenueSelector.Parse(input));

    [Fact]
    public void Parse_UnknownVenue_Throws() =>
        Assert.Throws<ArgumentException>(() => VenueSelector.Parse("kraken"));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter FullyQualifiedName~VenueSelectorTests`
Expected: FAIL — undefined.

- [ ] **Step 3: Implement**

```csharp
// VenueKind.cs
namespace AlgoTradeForge.LiveHost.Infrastructure.Live;
public enum VenueKind { Binance, Ib }
```

```csharp
// VenueSelector.cs
namespace AlgoTradeForge.LiveHost.Infrastructure.Live;

// Maps the host "Venue" config key to the active venue. One venue per LiveHost process
// (service-decomposition: N instances by venue class). Plan 5 maps ATF_PROFILE -> this key.
public static class VenueSelector
{
    public static VenueKind Parse(string? venue) => (venue ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "binance" => VenueKind.Binance,
        "ib" => VenueKind.Ib,
        var other => throw new ArgumentException($"Unknown Venue '{other}'. Expected 'binance' or 'ib'."),
    };
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter FullyQualifiedName~VenueSelectorTests`
Expected: PASS.

- [ ] **Step 5: Wire Program.cs**

In `Program.cs`, after binding options, branch the data-plane registrations:

```csharp
var venue = VenueSelector.Parse(builder.Configuration.GetValue<string>("Venue"));
builder.Services.Configure<IbDataPlaneOptions>(builder.Configuration.GetSection("Ib"));

if (venue == VenueKind.Ib)
{
    builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<IbDataPlaneOptions>>().Value);
    builder.Services.AddSingleton(sp => { var o = sp.GetRequiredService<IbDataPlaneOptions>(); return new IbConnectionOptions(o.Host, o.Port, o.ClientId); });
    builder.Services.AddSingleton<IbWrapper>();
    builder.Services.AddSingleton<IbConnection>();
    builder.Services.AddSingleton<IIbMarketDataClient, IbConnectionMarketDataClient>();
    builder.Services.AddSingleton<IIbMarketDataSession, IbSession>();
    builder.Services.AddSingleton<IbSession>(sp => (IbSession)sp.GetRequiredService<IIbMarketDataSession>());
    builder.Services.AddSingleton<IIbContractDetailsClient, IbConnectionContractDetailsClient>();
    builder.Services.AddSingleton<IIbContractResolver, IbContractResolver>();
    builder.Services.AddSingleton<IIbHistoricalTicksClient, IbConnectionHistoricalTicksClient>();
    // instrument -> Asset via the existing IAssetRepository
    builder.Services.AddSingleton<Func<string, Asset>>(sp => { var repo = sp.GetRequiredService<IAssetRepository>(); return name => repo.GetByName(name); });
    builder.Services.AddSingleton<IBackfillRequester>(sp => new IbBackfillRequester(
        sp.GetRequiredService<IIbHistoricalTicksClient>(), sp.GetRequiredService<IFileStorage>(),
        sp.GetRequiredService<CatchupOptions>().RelayKeyPrefix, sp.GetRequiredService<IbDataPlaneOptions>(),
        sp.GetRequiredService<IIbContractResolver>(), sp.GetRequiredService<Func<string, Asset>>(), TimeProvider.System));
    builder.Services.AddSingleton<IBarSourceResolver>(sp => new IbBarSourceResolver(
        sp.GetRequiredService<IIbMarketDataSession>(), sp.GetRequiredService<IIbContractResolver>(),
        sp.GetRequiredService<Func<string, Asset>>(), sp.GetRequiredService<IReplaySource>(),
        sp.GetRequiredService<IBackfillRequester>(), sp.GetRequiredService<IInt64BarLoader>(),
        sp.GetRequiredService<IbDataPlaneOptions>(), sp.GetRequiredService<CatchupOptions>()));
    builder.Services.AddSingleton<IVenueConnector>(sp => new IbVenueConnector(
        sp.GetRequiredService<IIbMarketDataSession>(), sp.GetRequiredService<IIbContractResolver>(),
        sp.GetRequiredService<Func<string, Asset>>(), sp.GetRequiredService<IbDataPlaneOptions>()));
}
else
{
    // existing Binance registrations (BinanceWebSocketManager, BarSourceResolver, BinanceVenueConnector,
    // BinanceBackfillRequester) move into this branch verbatim.
}
```

> Adjust `IAssetRepository`'s lookup method name to the real one (`GetByName`/`Resolve`/etc. — check `FileSystemAssetRepository`). Move the existing Binance `IVenueConnector` + `IBarSourceResolver` + `IBackfillRequester` + `BinanceWebSocketManager` registrations into the `else` branch unchanged. `IReplaySource` (`RelayArchiveReplaySource`) is venue-agnostic and stays shared.

- [ ] **Step 6: Build + boot smoke**

Run: `dotnet build AlgoTradeForge.slnx`
Expected: succeeds. (Full host boot under `Venue=ib` is exercised by Task 15's gated test; default `Venue` unset keeps Binance wiring — verify the existing WebApi tests still pass.)

Run: `dotnet test tests/AlgoTradeForge.LiveHost.WebApi.Tests/`
Expected: PASS (Binance default path unchanged).

- [ ] **Step 7: Commit** *(controller)*

```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/VenueKind.cs \
        src/AlgoTradeForge.LiveHost.Infrastructure/Live/VenueSelector.cs \
        src/AlgoTradeForge.LiveHost.WebApi/Program.cs \
        tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/VenueSelectorTests.cs
git commit -m "feat(livehost): config-driven venue selection + IB data-plane host wiring"
```

---

### Task 13: `IbRoundTripTests` — row-exact canonical CSV (the headline verification)

**Files:**
- Test: `tests/AlgoTradeForge.LiveHost.WebApi.Tests/IbRoundTripTests.cs`

**Interfaces:**
- Consumes: `IbVenueConnector`, a fake `IIbMarketDataSession`, `RelayPumpHostedService`, `StreamCanonicalizer<TradeTick>`, `TradeProjection`, `DailyTickCsvWriter` (mirror `LiveRoundTripTests`).

- [ ] **Step 1: Write the round-trip test**

Mirror `LiveRoundTripTests.LiveTicks_RoundTrip_To_CanonicalCsv_Lossless`, but feed IB ticks through `IbVenueConnector` (driven by a fake session) instead of a `FakeVenueConnector`. The fake session emits two `IbTradeUpdate`s on subscribe; the connector scales them; the pump archives; the canonicalizer produces CSV.

```csharp
// IbRoundTripTests.cs — key assertions (full harness mirrors LiveRoundTripTests)
[Fact]
public async Task IbTicks_RoundTrip_To_CanonicalCsv_Lossless()
{
    // AAPL scale (2,0): price ×100, qty ×1. Two ticks at Ts1 (sec) → archived → canonical CSV.
    // Build IbVenueConnector over a fake IIbMarketDataSession that, on SubscribeTrades, pushes:
    //   (timeSec=Ts1Sec, price=296.98, size=3)  and  (Ts1Sec, price=296.99, size=1)
    // Pump via RelayPumpHostedService.RunPumpOnce, then canonicalize with StreamCanonicalizer<TradeTick>.
    ...
    var lines = await _storage.ReadAllLines(csvKey, Ct);
    Assert.Equal("ts,price,qty,is_buyer_maker,agg_id", lines[0]);
    // Unknown aggressor → is_buyer_maker = 0; price 296.98×100 = 29698; qty 3; synthetic agg_id = 1
    Assert.Equal($"{Ts1Ms},29698,3,0,1", lines[1]);
    Assert.Equal($"{Ts1Ms},29699,1,0,2", lines[2]);
}
```

> **Confirm the `is_buyer_maker` mapping for `AggressorSide.Unknown`.** Open `TradeProjection` (HistoryLoader canonicalization) and check how it derives `is_buyer_maker` from `Aggressor`. `LiveRoundTripTests` shows `Sell→1`, `Buy→0`. Assert whatever the projection actually emits for `Unknown` (likely `0`); if the projection throws or has no `Unknown` branch, that is a real gap — fix the projection to map `Unknown→0` (IB ticks are legitimately side-less) with its own unit test, then assert here.

- [ ] **Step 2: Run to verify it fails, then passes**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.WebApi.Tests/ --filter FullyQualifiedName~IbRoundTripTests`
Expected: FAIL first (harness/types), then PASS after wiring the fake session + connector.

- [ ] **Step 3: Commit** *(controller)*

```bash
git add tests/AlgoTradeForge.LiveHost.WebApi.Tests/IbRoundTripTests.cs
# + any TradeProjection Unknown-aggressor fix
git commit -m "test(livehost): IB ticks round-trip to row-exact canonical CSV"
```

---

### Task 14: Residual (c) — faulted resolution is not cached

**Files:**
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers/IbContractResolverTests.cs` (extend)

**Interfaces:** Consumes existing `IbContractResolver` + `IIbContractDetailsClient`.

- [ ] **Step 1: Write the test**

```csharp
// append to IbContractResolverTests.cs
[Fact]
public async Task Resolve_FaultedFetch_IsNotCached_AndRetried()
{
    var spec = Spec();
    var client = Substitute.For<IIbContractDetailsClient>();
    client.FetchContractDetails(spec, Arg.Any<CancellationToken>())
        .Returns(
            _ => throw new IbRequestException(200, "No security definition has been found"),
            _ => Task.FromResult(new ResolvedIbContract(spec, 265598, "AAPL", "")));
    var resolver = new IbContractResolver(client);

    await Assert.ThrowsAsync<IbRequestException>(() => resolver.Resolve(spec, TestContext.Current.CancellationToken));
    var ok = await resolver.Resolve(spec, TestContext.Current.CancellationToken); // second attempt succeeds — not cached as faulted

    Assert.Equal(265598, ok.ConId);
    await client.Received(2).FetchContractDetails(spec, Arg.Any<CancellationToken>());
}
```

- [ ] **Step 2: Run**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter FullyQualifiedName~IbContractResolverTests`
Expected: PASS — the Plan-1 resolver already awaits before caching, so a throw never populates `_cache`. (If it FAILS, the resolver is caching faults — fix `IbContractResolver.Resolve` to only cache after a successful await.)

- [ ] **Step 3: Commit** *(controller)*

```bash
git add tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers/IbContractResolverTests.cs
git commit -m "test(livehost): IbContractResolver does not cache faulted resolutions (Plan-1 residual c)"
```

---

### Task 15: Gated paper integration

**Files:**
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers/IbDataPlanePaperTests.cs`

**Interfaces:** Consumes `IbPaperGatewayConfig` (existing), `IbConnection`, `IbWrapper`, `IbSession`, `IbVenueConnector`, `IbConnectionContractDetailsClient`, `IbContractResolver`.

- [ ] **Step 1: Write the gated integration tests**

```csharp
// IbDataPlanePaperTests.cs
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

[Trait("Category", "IbPaper")]
public class IbDataPlanePaperTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [SkippableFact]
    public async Task Connect_Resolve_StreamTicks_AAPL()
    {
        Skip.IfNot(IbPaperGatewayConfig.IsConfigured, IbPaperGatewayConfig.SkipReason);

        var wrapper = new IbWrapper();
        await using var connection = new IbConnection(wrapper, IbPaperGatewayConfig.Options);
        var session = new IbSession(new IbConnectionMarketDataClient(connection), wrapper);
        // ... resolve AAPL via IbConnectionContractDetailsClient + IbContractResolver,
        //     build IbVenueConnector, Stream(["AAPL"]) for a few seconds, assert >=1 TradeEvent OR
        //     (off-hours / no entitlement) assert a clean connect + subscription without throw.
        ...
    }
}
```

> Use `Skip.IfNot` (xunit.v3 `SkippableFact` is already in use per Plan 1's gated tests — match whatever attribute `IbContractResolverPaperTests.cs` uses). The **lossless historical-backfill live assertion is intentionally NOT asserted here** — AAPL paper data hits `10189 (no market-data subscription)`. Add a `[SkippableFact]` that `Skip`s with reason "requires IB market-data entitlement (10189)" so the gap is visible in test output, not silently absent.

- [ ] **Step 2: Run (skips without env)**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "Category=IbPaper"`
Expected: SKIPPED (CI / no gateway). With `IB_PAPER_HOST` set + the gnzsnz stack running, the connect/resolve/stream test runs.

- [ ] **Step 3: Full regression sweep**

Run sequentially:
```bash
dotnet build AlgoTradeForge.slnx
dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/
dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/
dotnet test tests/AlgoTradeForge.LiveHost.WebApi.Tests/
```
Expected: build 0/0; all green; IB paper tests skipped.

- [ ] **Step 4: Commit** *(controller)*

```bash
git add tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers/IbDataPlanePaperTests.cs
git commit -m "test(livehost): gated IB paper integration (connect, resolve, stream; backfill assertion entitlement-gated)"
```

---

## Self-Review

**1. Spec coverage**
- IbSession around IbConnection+IbWrapper → Tasks 4, 6. ✓
- IbVenueConnector push→channel→pull bridge → Task 7. ✓
- InstrumentScale configured exponents → Task 7. ✓
- IbVenueBarSource 5s venue bars → Task 8. ✓
- IBarSourceResolver venue-bar case + IB alt-bar catch-up + time gate + Renko fenced → Tasks 1, 11. ✓
- reqHistoricalTicks real backfill (option C) → Tasks 9, 10. ✓
- Reconnect trigger + re-subscribe → Tasks 3, 6. ✓
- MarketDataSessionPolicy host wiring / Venue selection → Task 12. ✓
- Residual (a) pump Join → Task 4; (b) nextValidId reconnect → Task 3; (c) faulted-not-cached → Task 14. ✓
- Row-exact canonical CSV → Task 13; venue 5s bars resolve → Task 11; gated paper → Task 15. ✓
- Aggressor=Unknown + synthetic sequence → Task 7 (+ TradeProjection check in Task 13). ✓

**2. Placeholder scan:** Three `...` markers remain and are intentional *harness scaffolding* (FakeIbSession bodies, the paper-test body, the round-trip harness) where the surrounding text fully specifies what to build and which existing test to mirror (`LiveRoundTripTests`, `IbContractResolverTests`). They are not logic placeholders — every production type is fully specified. No "TBD/add error handling/handle edge cases" placeholders.

**3. Type consistency:** `IIbMarketDataSession` (Task 6→7) is the seam the connector + bar source + resolver all consume (Tasks 7, 8, 11). `IbDataPlaneOptions` fields (`MaxGapMs`, `InstrumentScales`, `DefaultScale`) are consumed consistently (Tasks 7, 10, 11). `IbTradeUpdate`/`IbRealtimeBar`/`IbHistoricalTick` defined in Tasks 2/3, consumed in 6–10. `TickScale` reused from Binance namespace throughout. `TimeWatermarkGate(maxGapMs)` (Task 1) consumed in Task 11. Backfill ctor gains `IIbContractResolver` + `Func<string,Asset>` (Task 10 note) consistent with Task 12 wiring.

## Known follow-ups (documented, not in this plan)

- minTick-derived scale (sync-seam timing) — configured exponents for now.
- Same-millisecond tick dedup precision in `TimeWatermarkGate` — bar-level suppress covers re-derived bars; refine if alt-bar M6-style exactness is later required for IB.
- Renko IB catch-up — fenced (path-dependent `ReplayBoundary`, same as Binance).
- Live lossless-backfill assertion — entitlement-gated (`10189`).
- Order plane (Plan 4) shares `IbSession`/`IIbMarketDataClient` — the session's subscribe/reconnect seam accepts an order sub-lane without reshaping.
