# HistoryLoader Binary-Tick Canonicalizer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** HistoryLoader tails the Plan-1 binary `.atft` relay streams (`trades`, `quotes`, `_session`) off `IFileStorage` and canonicalizes them into its existing decimal, daily-partitioned CSV feeds — incrementally (cursor + CAS), idempotently (writer watermark), and as the sole canonicalizer.

**Architecture:** A generic, open/closed consumer mirroring the producer's `IFramePayload<T>`: a generic `StreamCanonicalizer<T>` tail loop drives a per-type `IStreamProjection<T>` that decodes one frame and writes a `FeedRecord` through the existing `BufferedPartitionWriter` machinery. Three projections (Trade→`ITickFeedWriter`, Quote→`IBookTickerWriter`, Session→a new `ISessionFeedWriter`). A per-`(venue,instrument,stream)` cursor stored under CAS bounds the scan; the writers' `agg_id`/`update_id`/`ts` watermark dedups at the crash boundary. A config-gated `BackgroundService` in `HistoryLoader.WebApi` discovers streams under the configured `live-md/{venue}/` prefix and dispatches to the canonicalizer matching each stream name.

**Tech Stack:** C# 14 / .NET 10, xUnit + (no mocks needed — concrete `LocalFileStorage`), `AlgoTradeForge.Live.Relay` (`SegmentReader<T>`/`SegmentWriter<T>`/`SegmentHeader`), `AlgoTradeForge.Domain.History` (`IFramePayload<T>`, `TradeTick`/`QuoteTick`/`SessionEvent`), `AlgoTradeForge.Storage` (`IFileStorage` CAS via `ReadWithEtag`/`WriteIfMatch`), `BufferedPartitionWriter`.

## Global Constraints

- **One type per file**, named after the type (Constitution v1.9.0). Single-line companion records may share the interface file.
- **Async I/O convention (v1.8.3):** async methods take `CancellationToken ct = default`; **no `Async` suffix** on new methods; no sync-over-async.
- **`using` over `try`/`finally`** for single-release cleanup (v1.9.1).
- **Int64 Money Convention:** the canonicalizer *un-scales* relay scaled-longs back to human-readable decimals using the segment header's `PriceScaleExp`/`QtyScaleExp`. Never raw `(long)` casts for money — but here we *read* longs and divide; that's the boundary-conversion path.
- **Comment convention (v1.8.4):** terse; only for non-obvious algorithm/pitfall/TODO. No signature restatement.
- **BG-service catch filter:** never `catch when (ex is not OperationCanceledException)`. Use the `IsTrueShutdown(ex, ct)` helper pattern (replicated from `ScheduledCollectorService`). HttpClient/storage timeouts must not crash the host.
- **xUnit analyzers:** `Assert.Single`/`Assert.Empty`, not `Assert.Equal(1/0, …)`.
- **Build/test ONE dotnet process at a time**, strictly sequential. Use `powershell.exe`, not `pwsh`.
- **Commit messages via bash heredoc + `git commit -F`**, never PowerShell `Out-File` (injects a UTF-8 BOM). Standing branch authorization granted for `feat/livehost-tick-canonicalizer`.
- **Build:** `dotnet build AlgoTradeForge.slnx`  · **Test:** `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/`

All new canonicalizer types live in namespace `AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization` unless stated otherwise. Tests live under `tests/AlgoTradeForge.HistoryLoader.Tests/Canonicalization/`.

---

## File Structure

**Create (Application):**
- `src/AlgoTradeForge.HistoryLoader.Application/Abstractions/ISessionFeedWriter.cs` — sink interface + `SessionResumeState`
- `src/AlgoTradeForge.HistoryLoader.Application/Canonicalization/CanonicalizerOptions.cs` — config

**Create (Infrastructure, `Canonicalization/`):**
- `SegmentLocation.cs`, `SegmentKeyParser.cs` — `.atft` key model
- `CanonicalScale.cs` — un-scale + aggressor mapping
- `InstrumentAssetDirMap.cs` — instrument→asset-dir resolution
- `IStreamProjection.cs` — per-type seam
- `TradeProjection.cs`, `QuoteProjection.cs`, `SessionProjection.cs`
- `IStreamCursorStore.cs` (+ `StreamCursor`), `FileStreamCursorStore.cs` — CAS cursor
- `IStreamCanonicalizer.cs`, `StreamCanonicalizer.cs` — generic tail loop
- `CanonicalizerServiceCollectionExtensions.cs` — DI

**Create (Infrastructure, `Storage/`):**
- `DailySessionCsvWriter.cs` — `ISessionFeedWriter` impl

**Create (WebApi):**
- `src/AlgoTradeForge.HistoryLoader.WebApi/Collection/TickCanonicalizerService.cs` — BackgroundService

**Modify:**
- `src/AlgoTradeForge.HistoryLoader.Infrastructure/AlgoTradeForge.HistoryLoader.Infrastructure.csproj` — add `AlgoTradeForge.Live.Relay` ProjectReference
- `src/AlgoTradeForge.HistoryLoader.Domain/FeedNames.cs` — add `Session`
- `src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs` — call `AddTickCanonicalizer()` + register BackgroundService

---

## Task 1: Segment key model + parser

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/AlgoTradeForge.HistoryLoader.Infrastructure.csproj`
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/SegmentLocation.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/SegmentKeyParser.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Canonicalization/SegmentKeyParserTests.cs`

**Interfaces:**
- Produces: `readonly record struct SegmentLocation(string Venue, string InstrumentOrVenue, string StreamName, long CreatedAtMs, long FirstSequence, string Key)`; `static bool SegmentKeyParser.TryParse(string key, string liveMdPrefix, out SegmentLocation loc)`.
- Consumes: nothing.

- [ ] **Step 1: Add the project reference** (the consumer side cannot see `SegmentReader<T>` without it)

In `AlgoTradeForge.HistoryLoader.Infrastructure.csproj`, add inside the existing `<ItemGroup>` of `ProjectReference`s:

```xml
    <ProjectReference Include="..\AlgoTradeForge.Live.Relay\AlgoTradeForge.Live.Relay.csproj" />
```

- [ ] **Step 2: Build to confirm no cycle**

Run: `dotnet build src/AlgoTradeForge.HistoryLoader.Infrastructure/AlgoTradeForge.HistoryLoader.Infrastructure.csproj`
Expected: PASS (Live.Relay → Domain/Storage only; no back-reference to Infrastructure).

- [ ] **Step 3: Write the failing test**

```csharp
using AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Canonicalization;

public sealed class SegmentKeyParserTests
{
    [Fact]
    public void TryParse_TradesKey_ExtractsAllParts()
    {
        var key = "live-md/binance/BTCUSDT/trades/0001700000000-0000000000000012345.atft";
        Assert.True(SegmentKeyParser.TryParse(key, "live-md", out var loc));
        Assert.Equal("binance", loc.Venue);
        Assert.Equal("BTCUSDT", loc.InstrumentOrVenue);
        Assert.Equal("trades", loc.StreamName);
        Assert.Equal(1700000000000, loc.CreatedAtMs);
        Assert.Equal(12345, loc.FirstSequence);
        Assert.Equal(key, loc.Key);
    }

    [Fact]
    public void TryParse_SessionKey_VenueOccupiesInstrumentSlot()
    {
        var key = "live-md/binance/binance/_session/0001700000000-0000000000000000000.atft";
        Assert.True(SegmentKeyParser.TryParse(key, "live-md", out var loc));
        Assert.Equal("binance", loc.Venue);
        Assert.Equal("binance", loc.InstrumentOrVenue);
        Assert.Equal("_session", loc.StreamName);
        Assert.Equal(0, loc.FirstSequence);
    }

    [Theory]
    [InlineData("live-md/binance/BTCUSDT/trades/not-a-segment.txt")]
    [InlineData("live-md/binance/BTCUSDT/trades")]
    [InlineData("other-prefix/binance/BTCUSDT/trades/0001700000000-0000000000000012345.atft")]
    public void TryParse_Malformed_ReturnsFalse(string key)
    {
        Assert.False(SegmentKeyParser.TryParse(key, "live-md", out _));
    }
}
```

- [ ] **Step 4: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter SegmentKeyParserTests`
Expected: FAIL — `SegmentLocation`/`SegmentKeyParser` not defined.

- [ ] **Step 5: Implement `SegmentLocation`**

```csharp
namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

public readonly record struct SegmentLocation(
    string Venue,
    string InstrumentOrVenue,
    string StreamName,
    long CreatedAtMs,
    long FirstSequence,
    string Key);
```

- [ ] **Step 6: Implement `SegmentKeyParser`**

Key form (matches `LocalSegmentSink` + `SegmentUploader`): `{liveMdPrefix}/{venue}/{instrumentOrVenue}/{stream}/{createdAtMs:D13}-{firstSeq:D19}.atft`. Both `venue` and `instrumentOrVenue` are positional — `_session` simply has the venue in both slots, so the parse is uniform.

```csharp
using System.Globalization;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

public static class SegmentKeyParser
{
    public static bool TryParse(string key, string liveMdPrefix, out SegmentLocation loc)
    {
        loc = default;
        if (string.IsNullOrEmpty(key) || !key.EndsWith(".atft", StringComparison.Ordinal)) return false;

        var prefix = liveMdPrefix.TrimEnd('/') + "/";
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var parts = key[prefix.Length..].Split('/');
        if (parts.Length != 4) return false;

        var venue = parts[0];
        var instrumentOrVenue = parts[1];
        var stream = parts[2];
        var file = parts[3];

        var name = file[..^".atft".Length];
        var dash = name.IndexOf('-');
        if (dash <= 0) return false;

        if (!long.TryParse(name[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out var createdAtMs)
            || !long.TryParse(name[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var firstSeq))
            return false;

        loc = new SegmentLocation(venue, instrumentOrVenue, stream, createdAtMs, firstSeq, key);
        return true;
    }
}
```

- [ ] **Step 7: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter SegmentKeyParserTests`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Infrastructure/AlgoTradeForge.HistoryLoader.Infrastructure.csproj \
        src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/SegmentLocation.cs \
        src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/SegmentKeyParser.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Canonicalization/SegmentKeyParserTests.cs
git commit -F - <<'EOF'
feat(canonicalizer): segment key model + parser

Parses uploaded .atft keys into SegmentLocation (venue, instrument-or-venue,
stream, createdAtMs, firstSeq). Adds the Live.Relay project reference to
HistoryLoader.Infrastructure (no cycle: Relay -> Domain/Storage only).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_018w22NAfM8bQwp5TTiMMGbX
EOF
```

---

## Task 2: Un-scale + aggressor mapping

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/CanonicalScale.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Canonicalization/CanonicalScaleTests.cs`

**Interfaces:**
- Produces: `static double CanonicalScale.Unscale(long raw, sbyte exp)`; `static double CanonicalScale.ToIsBuyerMaker(AggressorSide side)`.
- Consumes: `AggressorSide` (`AlgoTradeForge.Domain.History`).

- [ ] **Step 1: Write the failing test**

`is_buyer_maker` is `0 = buy-aggressive, 1 = sell-aggressive` (per `PartitionedSourceReader`). Un-scale inverts the relay's power-of-ten scaling: `decimal = raw / 10^exp`.

```csharp
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Canonicalization;

public sealed class CanonicalScaleTests
{
    [Theory]
    [InlineData(5000050L, 2, 50000.5)]   // price exp 2
    [InlineData(123L, 3, 0.123)]         // qty exp 3
    [InlineData(42L, 0, 42.0)]           // exp 0 == identity
    [InlineData(5L, -1, 50.0)]           // negative exp multiplies
    public void Unscale_DividesByPowerOfTen(long raw, int exp, double expected)
    {
        Assert.Equal(expected, CanonicalScale.Unscale(raw, (sbyte)exp), precision: 10);
    }

    [Theory]
    [InlineData(AggressorSide.Sell, 1.0)]
    [InlineData(AggressorSide.Buy, 0.0)]
    [InlineData(AggressorSide.Unknown, 0.0)]
    public void ToIsBuyerMaker_SellIsOne(AggressorSide side, double expected)
    {
        Assert.Equal(expected, CanonicalScale.ToIsBuyerMaker(side));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter CanonicalScaleTests`
Expected: FAIL — `CanonicalScale` not defined.

- [ ] **Step 3: Implement `CanonicalScale`**

```csharp
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

public static class CanonicalScale
{
    // Inverts the relay's power-of-ten scaling. decimal-exact, then widened to the
    // double the canonical CSV writers persist.
    public static double Unscale(long raw, sbyte exp)
    {
        decimal scale = Pow10(Math.Abs(exp));
        decimal value = exp >= 0 ? raw / scale : raw * scale;
        return (double)value;
    }

    public static double ToIsBuyerMaker(AggressorSide side) =>
        side == AggressorSide.Sell ? 1.0 : 0.0;

    private static decimal Pow10(int n)
    {
        decimal r = 1m;
        for (int i = 0; i < n; i++) r *= 10m;
        return r;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter CanonicalScaleTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/CanonicalScale.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Canonicalization/CanonicalScaleTests.cs
git commit -F - <<'EOF'
feat(canonicalizer): header-driven un-scale + aggressor mapping

Unscale(raw, exp) inverts the relay's power-of-ten scaling using the segment
header exps (retires the Plan-1 hardcoded (2,0) debt). is_buyer_maker:
Sell -> 1, else 0.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_018w22NAfM8bQwp5TTiMMGbX
EOF
```

---

## Task 3: `ISessionFeedWriter` + `DailySessionCsvWriter`

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Domain/FeedNames.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Abstractions/ISessionFeedWriter.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Storage/DailySessionCsvWriter.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Storage/DailySessionCsvWriterTests.cs`

**Interfaces:**
- Produces: `readonly record struct SessionResumeState(long LastTsMs)`; `interface ISessionFeedWriter { void Write(string venueDir, FeedRecord record); Task<SessionResumeState?> ResumeFrom(string venueDir, CancellationToken ct = default); }`; `FeedNames.Session == "_session"`.
- Consumes: `FeedRecord`, `BufferedPartitionWriter`, `IFileStorage`, `IPartitionTailIndex`, `HistoryLoaderStorageOptions`, `WriteLockManager`.

- [ ] **Step 1: Add the feed name**

In `FeedNames.cs`, add:

```csharp
    public const string Session = "_session";
```

- [ ] **Step 2: Write the failing test**

Mirrors `DailyTickCsvWriterTests` fixture exactly. Schema `ts,kind`; dedup by `ts`.

```csharp
using AlgoTradeForge.Storage;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Storage;

public sealed class DailySessionCsvWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LocalFileStorage _storage;
    private readonly LocalTailIndex _tail;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly long Ts =
        new DateTimeOffset(2024, 3, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public DailySessionCsvWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"DailySessionCsvWriterTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _tempDir });
        _tail = new LocalTailIndex(_storage);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private DailySessionCsvWriter NewWriter() => new(
        _storage, _tail,
        Options.Create(new HistoryLoaderStorageOptions { FlushEveryRows = 1, FlushIntervalSeconds = 60 }),
        NullLogger<DailySessionCsvWriter>.Instance, new WriteLockManager());

    private static FeedRecord Session(long ts, int kind) => new(ts, [kind]);

    private static string Key(long ts) =>
        Path.Combine(_TempStatic, FeedNames.Session,
            $"{DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime:yyyy-MM-dd}.csv");
    private static string _TempStatic = null!; // set per-instance below

    [Fact]
    public async Task Write_NewFile_CreatesHeaderAndRow()
    {
        _TempStatic = _tempDir;
        var w = NewWriter();
        w.Write(_tempDir, Session(Ts, kind: 1)); // SessionStart
        await w.FlushAllAsync(Ct);

        var lines = await _storage.ReadAllLines(Key(Ts), Ct);
        Assert.Equal("ts,kind", lines[0]);
        Assert.Equal($"{Ts},1", lines[1]);
    }

    [Fact]
    public async Task Write_DedupsByTimestamp()
    {
        _TempStatic = _tempDir;
        var w = NewWriter();
        w.Write(_tempDir, Session(Ts, 0));
        w.Write(_tempDir, Session(Ts, 0));      // same ts -> dropped
        w.Write(_tempDir, Session(Ts + 1, 0));
        await w.FlushAllAsync(Ct);

        var lines = await _storage.ReadAllLines(Key(Ts), Ct);
        Assert.Equal(3, lines.Length); // header + 2
    }

    [Fact]
    public async Task ResumeFrom_CleanFile_ReturnsLastTs()
    {
        _TempStatic = _tempDir;
        var w = NewWriter();
        w.Write(_tempDir, Session(Ts, 0));
        w.Write(_tempDir, Session(Ts + 1000, 0));
        await w.FlushAllAsync(Ct);

        var resume = await NewWriter().ResumeFrom(_tempDir, Ct);
        Assert.NotNull(resume);
        Assert.Equal(Ts + 1000, resume!.Value.LastTsMs);
    }
}
```

> Note for the implementer: the `_TempStatic` shuffle above only exists to keep the `Key` helper static; if you prefer, inline the partition-key computation into each test. Behaviour asserted is what matters.

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter DailySessionCsvWriterTests`
Expected: FAIL — `ISessionFeedWriter`/`DailySessionCsvWriter` not defined.

- [ ] **Step 4: Implement `ISessionFeedWriter`**

```csharp
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

/// <summary>Last <c>ts</c> persisted for the latest daily <c>_session</c> partition.</summary>
public readonly record struct SessionResumeState(long LastTsMs);

/// <summary>
/// Daily-partitioned per-venue liveness writer (<c>{venueDir}/_session/&lt;YYYY-MM-DD&gt;.csv</c>,
/// schema <c>ts,kind</c>). Dedup by <c>ts</c> — heartbeats are emitted in monotonic time order.
/// </summary>
public interface ISessionFeedWriter
{
    /// <summary>Values must be <c>[kind]</c>. Records whose <c>ts</c> is at-or-below the
    /// partition watermark are silently dropped.</summary>
    void Write(string venueDir, FeedRecord record);

    Task<SessionResumeState?> ResumeFrom(string venueDir, CancellationToken ct = default);
}
```

- [ ] **Step 5: Implement `DailySessionCsvWriter`** (mirror `DailyTickCsvWriter`)

```csharp
using System.Globalization;
using AlgoTradeForge.Storage;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage.Buffered;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Storage;

internal sealed class DailySessionCsvWriter : BufferedPartitionWriter, ISessionFeedWriter
{
    private const int ValueCount = 1; // [kind]
    private const string Header = "ts,kind";

    public DailySessionCsvWriter(
        IFileStorage storage,
        IPartitionTailIndex tailIndex,
        IOptions<HistoryLoaderStorageOptions> options,
        ILogger<DailySessionCsvWriter> logger,
        WriteLockManager locks)
        : base(storage, tailIndex, options, logger, locks) { }

    public void Write(string venueDir, FeedRecord record)
    {
        if (record.Values.Length != ValueCount)
            throw new ArgumentException(
                $"Session FeedRecord must have {ValueCount} value [kind]; got {record.Values.Length}.",
                nameof(record));

        var partitionKey = GetPartitionKey(venueDir, record.TimestampMs);
        var row =
            $"{record.TimestampMs.ToString(CultureInfo.InvariantCulture)}," +
            $"{((int)record.Values[0]).ToString(CultureInfo.InvariantCulture)}";

        AppendRow(partitionKey, Header, row, record.TimestampMs);
    }

    public async Task<SessionResumeState?> ResumeFrom(string venueDir, CancellationToken ct = default)
    {
        var feedDir = Path.Combine(venueDir, FeedNames.Session);
        if (!Directory.Exists(feedDir)) return null;

        var files = Directory.GetFiles(feedDir, "????-??-??.csv")
            .OrderByDescending(f => f, StringComparer.Ordinal).ToArray();
        if (files.Length == 0) return null;

        var latestFile = files[0];
        var lastLine = await TailIndex.GetLastLine(latestFile, ct);
        if (lastLine is null
            || lastLine.StartsWith("ts,", StringComparison.Ordinal)
            || lastLine.Equals("ts", StringComparison.OrdinalIgnoreCase))
            return null;

        var parts = lastLine.Split(',');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts))
            return null;

        RegisterPartitionWatermark(latestFile, Header, ts);
        return new SessionResumeState(ts);
    }

    private static string GetPartitionKey(string venueDir, long timestampMs)
    {
        var dayKey = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs)
            .UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Path.Combine(venueDir, FeedNames.Session, $"{dayKey}.csv");
    }
}
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter DailySessionCsvWriterTests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Domain/FeedNames.cs \
        src/AlgoTradeForge.HistoryLoader.Application/Abstractions/ISessionFeedWriter.cs \
        src/AlgoTradeForge.HistoryLoader.Infrastructure/Storage/DailySessionCsvWriter.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Storage/DailySessionCsvWriterTests.cs
git commit -F - <<'EOF'
feat(canonicalizer): per-venue _session daily CSV sink

New ISessionFeedWriter + DailySessionCsvWriter ({venueDir}/_session/<day>.csv,
schema ts,kind). Dedup by ts (heartbeats are monotonic). Mirrors the tick/book
writers on BufferedPartitionWriter.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_018w22NAfM8bQwp5TTiMMGbX
EOF
```

---

## Task 4: Projection seam + three projections + asset-dir map

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/InstrumentAssetDirMap.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/IStreamProjection.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/TradeProjection.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/QuoteProjection.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/SessionProjection.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Canonicalization/ProjectionTests.cs`

**Interfaces:**
- Consumes: `SegmentLocation`, `CanonicalScale`, `SegmentHeader` (`AlgoTradeForge.Live.Relay`), `TradeTick`/`QuoteTick`/`SessionEvent`/`IFramePayload<T>` (`AlgoTradeForge.Domain.History`), `ITickFeedWriter`/`IBookTickerWriter`/`ISessionFeedWriter`, `IBufferedPartitionWriter`.
- Produces:
  - `interface IStreamProjection<T> where T : IFramePayload<T> { void Apply(in T frame, in SegmentHeader header, SegmentLocation loc); Task Seed(SegmentLocation loc, CancellationToken ct); Task Flush(CancellationToken ct); }`
  - `sealed class InstrumentAssetDirMap` with `string Resolve(string venue, string instrument)` and `string VenueDir(string venue)`.
  - `TradeProjection`, `QuoteProjection`, `SessionProjection`.

- [ ] **Step 1: Write the failing test**

`InstrumentAssetDirMap` resolves to absolute dirs (the writers' `ResumeFrom` does `Directory.GetFiles`, so dirs must be real FS paths; this matches `DailyTickCsvWriterTests` passing an absolute `assetDir`).

```csharp
using AlgoTradeForge.Storage;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Canonicalization;

public sealed class ProjectionTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileStorage _storage;
    private readonly LocalTailIndex _tail;
    private readonly InstrumentAssetDirMap _map;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly long Ts =
        new DateTimeOffset(2024, 3, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public ProjectionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ProjectionTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _root });
        _tail = new LocalTailIndex(_storage);
        _map = new InstrumentAssetDirMap(_root, new Dictionary<string, string>());
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static SegmentHeader Header(sbyte p, sbyte q) =>
        new(p, q, EpochBaseMs: 0, CreatedAtMs: Ts, FirstSequence: 0, PayloadSize: 0);

    private static SegmentLocation Loc(string stream) =>
        new("binance", "BTCUSDT", stream, Ts, 0, $"live-md/binance/BTCUSDT/{stream}/x.atft");

    private DailyTickCsvWriter TickWriter() => new(
        _storage, _tail, Options.Create(new HistoryLoaderStorageOptions { FlushEveryRows = 1, FlushIntervalSeconds = 60 }),
        NullLogger<DailyTickCsvWriter>.Instance, new WriteLockManager());

    [Fact]
    public async Task TradeProjection_WritesUnscaledDecimalRow()
    {
        var writer = TickWriter();
        var proj = new TradeProjection(writer, _map);
        await proj.Seed(Loc("trades"), Ct);

        // price 5000050 @ exp2 -> 50000.5 ; qty 123 @ exp3 -> 0.123 ; Sell -> is_buyer_maker 1 ; seq 77
        proj.Apply(new TradeTick(Ts, 5000050, 123, 77, AggressorSide.Sell), Header(2, 3), Loc("trades"));
        await proj.Flush(Ct);

        var assetDir = _map.Resolve("binance", "BTCUSDT");
        var key = Path.Combine(assetDir, "ticks",
            $"{DateTimeOffset.FromUnixTimeMilliseconds(Ts).UtcDateTime:yyyy-MM-dd}.csv");
        var lines = await _storage.ReadAllLines(key, Ct);
        Assert.Equal("ts,price,qty,is_buyer_maker,agg_id", lines[0]);
        Assert.Equal($"{Ts},50000.5,0.123,1,77", lines[1]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter ProjectionTests`
Expected: FAIL — types not defined.

- [ ] **Step 3: Implement `InstrumentAssetDirMap`**

```csharp
namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

/// <summary>
/// Resolves a relay instrument string (e.g. "BTCUSDT") to the canonical asset directory the
/// CSV writers partition under. Defaults to <c>{baseDir}/{venue}/{instrument}</c>; explicit
/// overrides (keyed by instrument) carry venue-specific naming such as the <c>_perp</c> suffix.
/// </summary>
public sealed class InstrumentAssetDirMap(string baseDir, IReadOnlyDictionary<string, string> overrides)
{
    public string Resolve(string venue, string instrument) =>
        overrides.TryGetValue(instrument, out var dir)
            ? Path.Combine(baseDir, dir)
            : Path.Combine(baseDir, venue, instrument);

    public string VenueDir(string venue) => Path.Combine(baseDir, venue);
}
```

- [ ] **Step 4: Implement `IStreamProjection<T>`**

```csharp
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

/// <summary>
/// Per-type consumer slice: decodes one relay frame and writes its canonical row through the
/// stream's existing CSV sink. Mirrors the producer's IFramePayload&lt;T&gt; — adding a stream
/// type is a new projection + one registration, no edits to the tail loop.
/// </summary>
public interface IStreamProjection<T> where T : IFramePayload<T>
{
    void Apply(in T frame, in SegmentHeader header, SegmentLocation loc);
    Task Seed(SegmentLocation loc, CancellationToken ct);   // seed the writer dedup watermark
    Task Flush(CancellationToken ct);                        // durable publish before cursor advance
}
```

- [ ] **Step 5: Implement `TradeProjection`**

```csharp
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage.Buffered;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

internal sealed class TradeProjection(ITickFeedWriter writer, InstrumentAssetDirMap map)
    : IStreamProjection<TradeTick>
{
    public void Apply(in TradeTick frame, in SegmentHeader header, SegmentLocation loc)
    {
        var assetDir = map.Resolve(loc.Venue, loc.InstrumentOrVenue);
        writer.Write(assetDir, new FeedRecord(frame.TimestampMs,
        [
            CanonicalScale.Unscale(frame.Price, header.PriceScaleExp),
            CanonicalScale.Unscale(frame.Quantity, header.QtyScaleExp),
            CanonicalScale.ToIsBuyerMaker(frame.Aggressor),
            frame.Sequence
        ]));
    }

    public Task Seed(SegmentLocation loc, CancellationToken ct) =>
        writer.ResumeFrom(map.Resolve(loc.Venue, loc.InstrumentOrVenue), ct);

    public Task Flush(CancellationToken ct) =>
        ((IBufferedPartitionWriter)writer).FlushAllAsync(ct);
}
```

- [ ] **Step 6: Implement `QuoteProjection`** (prices use `PriceScaleExp`, sizes use `QtyScaleExp`)

```csharp
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage.Buffered;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

internal sealed class QuoteProjection(IBookTickerWriter writer, InstrumentAssetDirMap map)
    : IStreamProjection<QuoteTick>
{
    public void Apply(in QuoteTick frame, in SegmentHeader header, SegmentLocation loc)
    {
        var assetDir = map.Resolve(loc.Venue, loc.InstrumentOrVenue);
        writer.Write(assetDir, new FeedRecord(frame.TimestampMs,
        [
            CanonicalScale.Unscale(frame.BidPrice, header.PriceScaleExp),
            CanonicalScale.Unscale(frame.BidSize, header.QtyScaleExp),
            CanonicalScale.Unscale(frame.AskPrice, header.PriceScaleExp),
            CanonicalScale.Unscale(frame.AskSize, header.QtyScaleExp),
            frame.Sequence
        ]));
    }

    public Task Seed(SegmentLocation loc, CancellationToken ct) =>
        writer.ResumeFrom(map.Resolve(loc.Venue, loc.InstrumentOrVenue), ct);

    public Task Flush(CancellationToken ct) =>
        ((IBufferedPartitionWriter)writer).FlushAllAsync(ct);
}
```

- [ ] **Step 7: Implement `SessionProjection`** (`_session` maps to the venue dir, not an instrument)

```csharp
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage.Buffered;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

internal sealed class SessionProjection(ISessionFeedWriter writer, InstrumentAssetDirMap map)
    : IStreamProjection<SessionEvent>
{
    public void Apply(in SessionEvent frame, in SegmentHeader header, SegmentLocation loc)
    {
        var venueDir = map.VenueDir(loc.Venue);
        writer.Write(venueDir, new FeedRecord(frame.TimestampMs, [(double)(int)frame.Kind]));
    }

    public Task Seed(SegmentLocation loc, CancellationToken ct) =>
        writer.ResumeFrom(map.VenueDir(loc.Venue), ct);

    public Task Flush(CancellationToken ct) =>
        ((IBufferedPartitionWriter)writer).FlushAllAsync(ct);
}
```

- [ ] **Step 8: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter ProjectionTests`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/InstrumentAssetDirMap.cs \
        src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/IStreamProjection.cs \
        src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/TradeProjection.cs \
        src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/QuoteProjection.cs \
        src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/SessionProjection.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Canonicalization/ProjectionTests.cs
git commit -F - <<'EOF'
feat(canonicalizer): per-type projection seam + trade/quote/session projections

IStreamProjection<T> mirrors the producer's IFramePayload<T>: each projection
decodes one frame, un-scales via the segment header exps, and writes through the
existing CSV sink. Trade->ITickFeedWriter, Quote->IBookTickerWriter,
Session->ISessionFeedWriter. InstrumentAssetDirMap resolves instrument->asset dir.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_018w22NAfM8bQwp5TTiMMGbX
EOF
```

---

## Task 5: Cursor store (CAS) — **opus; concurrency-critical**

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/IStreamCursorStore.cs` (+ `StreamCursor`)
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/FileStreamCursorStore.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Canonicalization/FileStreamCursorStoreTests.cs`

**Interfaces:**
- Consumes: `IFileStorage.ReadWithEtag`/`WriteIfMatch`, `StoredObject(string Content, string ETag)`, `ConcurrencyConflictException`.
- Produces:
  - `readonly record struct StreamCursor(string? LastSegmentKey, string? ETag)`
  - `interface IStreamCursorStore { Task<StreamCursor> Read(string cursorKey, CancellationToken ct = default); Task<string> Advance(string cursorKey, string lastSegmentKey, string? expectedETag, CancellationToken ct = default); }`

- [ ] **Step 1: Write the failing test**

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter FileStreamCursorStoreTests`
Expected: FAIL — types not defined.

- [ ] **Step 3: Implement `IStreamCursorStore` + `StreamCursor`**

```csharp
namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

/// <summary>The last fully-consumed segment key for one stream, plus its CAS etag.</summary>
public readonly record struct StreamCursor(string? LastSegmentKey, string? ETag);

public interface IStreamCursorStore
{
    Task<StreamCursor> Read(string cursorKey, CancellationToken ct = default);

    /// <summary>Advances the cursor under CAS. Pass the etag from <see cref="Read"/> (or null
    /// for create). Throws <see cref="AlgoTradeForge.Storage.ConcurrencyConflictException"/> on
    /// a stale etag. Returns the new etag.</summary>
    Task<string> Advance(string cursorKey, string lastSegmentKey, string? expectedETag, CancellationToken ct = default);
}
```

- [ ] **Step 4: Implement `FileStreamCursorStore`**

```csharp
using AlgoTradeForge.Storage;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

internal sealed class FileStreamCursorStore(IFileStorage storage) : IStreamCursorStore
{
    public async Task<StreamCursor> Read(string cursorKey, CancellationToken ct = default)
    {
        var obj = await storage.ReadWithEtag(cursorKey, ct);
        return obj is null
            ? new StreamCursor(null, null)
            : new StreamCursor(obj.Content.Trim(), obj.ETag);
    }

    public Task<string> Advance(string cursorKey, string lastSegmentKey, string? expectedETag, CancellationToken ct = default) =>
        storage.WriteIfMatch(cursorKey, lastSegmentKey, expectedETag, ct);
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter FileStreamCursorStoreTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/IStreamCursorStore.cs \
        src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/FileStreamCursorStore.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Canonicalization/FileStreamCursorStoreTests.cs
git commit -F - <<'EOF'
feat(canonicalizer): CAS-protected per-stream cursor store

Stores the last fully-consumed segment key under a HistoryLoader-owned prefix,
advanced via IFileStorage.WriteIfMatch (CAS). A stale etag raises
ConcurrencyConflictException — defense-in-depth on sole-canonicalizer.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_018w22NAfM8bQwp5TTiMMGbX
EOF
```

---

## Task 6: Generic tail loop `StreamCanonicalizer<T>` — **opus; idempotency-critical**

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/IStreamCanonicalizer.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/StreamCanonicalizer.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Canonicalization/StreamCanonicalizerTests.cs`

**Interfaces:**
- Consumes: `IFileStorage.ListKeys`/`OpenRead`, `SegmentReader<T>`, `SegmentKeyParser`, `IStreamProjection<T>`, `IStreamCursorStore`, `CanonicalizerOptions` (defined here as a minimal shape; Task 7 moves the full options type to Application — keep the property names identical).
- Produces:
  - `interface IStreamCanonicalizer { string StreamName { get; } Task<int> Run(string venue, string instrumentOrVenue, CancellationToken ct = default); }`
  - `sealed class StreamCanonicalizer<T> : IStreamCanonicalizer where T : IFramePayload<T>` with ctor `(IFileStorage storage, IStreamProjection<T> projection, IStreamCursorStore cursors, string liveMdPrefix, string cursorPrefix)`.

> Implementer note: to keep this task self-contained, `StreamCanonicalizer<T>` takes `liveMdPrefix`/`cursorPrefix` as plain `string` ctor args (not an options object). Task 7 wires them from `CanonicalizerOptions`.

- [ ] **Step 1: Write the failing test** — round-trip + incremental-equals-batch + idempotency

```csharp
using AlgoTradeForge.Storage;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Canonicalization;

public sealed class StreamCanonicalizerTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileStorage _storage;
    private readonly LocalTailIndex _tail;
    private readonly InstrumentAssetDirMap _map;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly long Ts =
        new DateTimeOffset(2024, 3, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public StreamCanonicalizerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"StreamCanonTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _root });
        _tail = new LocalTailIndex(_storage);
        _map = new InstrumentAssetDirMap(_root, new Dictionary<string, string>());
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private StreamCanonicalizer<TradeTick> NewCanonicalizer()
    {
        var writer = new DailyTickCsvWriter(
            _storage, _tail,
            Options.Create(new HistoryLoaderStorageOptions { FlushEveryRows = 1, FlushIntervalSeconds = 60 }),
            NullLogger<DailyTickCsvWriter>.Instance, new WriteLockManager());
        var proj = new TradeProjection(writer, _map);
        var cursors = new FileStreamCursorStore(_storage);
        return new StreamCanonicalizer<TradeTick>(_storage, proj, cursors, "live-md", "_canon-cursors");
    }

    // Writes one synthetic .atft trades segment with the given trades; returns its storage key.
    private async Task<string> WriteSegment(long createdAtMs, long firstSeq, params TradeTick[] trades)
    {
        using var ms = new MemoryStream();
        using (var w = new SegmentWriter<TradeTick>(ms,
            new SegmentHeader(PriceScaleExp: 2, QtyScaleExp: 3, EpochBaseMs: 0,
                CreatedAtMs: createdAtMs, FirstSequence: firstSeq, PayloadSize: (ushort)TradeTick.PayloadSize),
            leaveOpen: true))
        {
            foreach (var t in trades) w.Write(t);
        }
        var key = $"live-md/binance/BTCUSDT/trades/{createdAtMs:D13}-{firstSeq:D19}.atft";
        await _storage.WriteAllBytes(key, ms.ToArray(), Ct);
        return key;
    }

    private async Task<string[]> CanonLines()
    {
        var key = Path.Combine(_map.Resolve("binance", "BTCUSDT"), "ticks",
            $"{DateTimeOffset.FromUnixTimeMilliseconds(Ts).UtcDateTime:yyyy-MM-dd}.csv");
        return await _storage.Exists(key, Ct) ? await _storage.ReadAllLines(key, Ct) : [];
    }

    [Fact]
    public async Task Run_TwoSegments_CanonicalizesAllTradesUnscaled()
    {
        await WriteSegment(Ts, 1, new TradeTick(Ts, 5000050, 123, 1, AggressorSide.Buy));
        await WriteSegment(Ts + 1, 2, new TradeTick(Ts + 5, 5000100, 200, 2, AggressorSide.Sell));

        var n = await NewCanonicalizer().Run("binance", "BTCUSDT", Ct);

        Assert.Equal(2, n);
        var lines = await CanonLines();
        Assert.Equal("ts,price,qty,is_buyer_maker,agg_id", lines[0]);
        Assert.Equal($"{Ts},50000.5,0.123,0,1", lines[1]);
        Assert.Equal($"{Ts + 5},50001,0.2,1,2", lines[2]);
    }

    [Fact]
    public async Task Run_Rerun_NoNewRows_Idempotent()
    {
        await WriteSegment(Ts, 1, new TradeTick(Ts, 5000050, 123, 1, AggressorSide.Buy));
        await NewCanonicalizer().Run("binance", "BTCUSDT", Ct);
        var afterFirst = await CanonLines();

        // A brand-new canonicalizer (cursor already persisted) over the same segment.
        var n2 = await NewCanonicalizer().Run("binance", "BTCUSDT", Ct);
        var afterSecond = await CanonLines();

        Assert.Equal(0, n2);                       // cursor skips the consumed segment
        Assert.Equal(afterFirst.Length, afterSecond.Length);
    }

    [Fact]
    public async Task Run_ReprocessWithoutCursor_WatermarkDedups()
    {
        // Simulate the crash window: rows flushed, cursor never advanced. Delete the cursor and
        // re-run; the writer's agg_id watermark must drop the already-written rows.
        await WriteSegment(Ts, 1, new TradeTick(Ts, 5000050, 123, 1, AggressorSide.Buy));
        await NewCanonicalizer().Run("binance", "BTCUSDT", Ct);

        await _storage.Delete("_canon-cursors/binance/BTCUSDT/trades.cursor", Ct);
        await NewCanonicalizer().Run("binance", "BTCUSDT", Ct);

        var lines = await CanonLines();
        Assert.Equal(2, lines.Length); // header + exactly one row (no duplicate)
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter StreamCanonicalizerTests`
Expected: FAIL — `StreamCanonicalizer`/`IStreamCanonicalizer` not defined.

- [ ] **Step 3: Implement `IStreamCanonicalizer`**

```csharp
namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

/// <summary>Non-generic seam so the dispatcher can hold a set of typed canonicalizers and route
/// by <see cref="StreamName"/> without knowing the payload type.</summary>
public interface IStreamCanonicalizer
{
    string StreamName { get; }
    Task<int> Run(string venue, string instrumentOrVenue, CancellationToken ct = default);
}
```

- [ ] **Step 4: Implement `StreamCanonicalizer<T>`**

```csharp
using System.Globalization;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.Storage;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

internal sealed class StreamCanonicalizer<T>(
    IFileStorage storage,
    IStreamProjection<T> projection,
    IStreamCursorStore cursors,
    string liveMdPrefix,
    string cursorPrefix) : IStreamCanonicalizer
    where T : IFramePayload<T>
{
    public string StreamName => T.StreamName;

    public async Task<int> Run(string venue, string instrumentOrVenue, CancellationToken ct = default)
    {
        var streamPrefix = $"{liveMdPrefix}/{venue}/{instrumentOrVenue}/{T.StreamName}/";
        var cursorKey = $"{cursorPrefix}/{venue}/{instrumentOrVenue}/{T.StreamName}.cursor";

        var cursor = await cursors.Read(cursorKey, ct);

        var keys = new List<string>();
        await foreach (var k in storage.ListKeys(streamPrefix, ".atft", recursive: true, ct))
            keys.Add(k);
        keys.Sort(StringComparer.Ordinal);

        var pending = cursor.LastSegmentKey is { } last
            ? keys.Where(k => string.CompareOrdinal(k, last) > 0).ToList()
            : keys;
        if (pending.Count == 0) return 0;

        // Seed the writer dedup watermark once from the canonical partitions before any append,
        // so a reprocessed boundary segment (crash between flush and cursor-advance) is a no-op.
        if (!SegmentKeyParser.TryParse(pending[0], liveMdPrefix, out var seedLoc)) return 0;
        await projection.Seed(seedLoc, ct);

        var etag = cursor.ETag;
        int frames = 0;
        foreach (var key in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (!SegmentKeyParser.TryParse(key, liveMdPrefix, out var loc)) continue;

            using (var reader = new SegmentReader<T>(await storage.OpenRead(key, ct)))
            {
                while (reader.TryRead(out var frame))
                {
                    projection.Apply(frame, reader.Header, loc);
                    frames++;
                }
            }

            await projection.Flush(ct);                              // durable publish ...
            etag = await cursors.Advance(cursorKey, key, etag, ct);  // ... then advance the cursor
        }
        return frames;
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter StreamCanonicalizerTests`
Expected: PASS (all three: round-trip, idempotent rerun, watermark-dedups-reprocess).

- [ ] **Step 6: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/IStreamCanonicalizer.cs \
        src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/StreamCanonicalizer.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Canonicalization/StreamCanonicalizerTests.cs
git commit -F - <<'EOF'
feat(canonicalizer): generic two-layer incremental tail loop

StreamCanonicalizer<T> lists segments past the cursor, decodes via
SegmentReader<T>, projects each frame, flushes, then advances the cursor under
CAS. Two-layer idempotency: cursor bounds the scan; writer watermark dedups a
reprocessed boundary segment. Covered by round-trip, idempotent-rerun, and
crash-reprocess tests.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_018w22NAfM8bQwp5TTiMMGbX
EOF
```

---

## Task 7: Options + DI + BackgroundService + host wiring

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Canonicalization/CanonicalizerOptions.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/CanonicalizerServiceCollectionExtensions.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.WebApi/Collection/TickCanonicalizerService.cs`
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Canonicalization/CanonicalizerDispatchTests.cs`

**Interfaces:**
- Produces: `CanonicalizerOptions` (config); `IServiceCollection AddTickCanonicalizer(this IServiceCollection)`; `TickCanonicalizerService : BackgroundService`.
- Consumes: `IStreamCanonicalizer` (set), `IFileStorage`, `SegmentKeyParser`, `IsTrueShutdown` pattern.

- [ ] **Step 1: Implement `CanonicalizerOptions`**

```csharp
namespace AlgoTradeForge.HistoryLoader.Application.Canonicalization;

public sealed class CanonicalizerOptions
{
    public const string SectionName = "Canonicalizer";

    public bool Enabled { get; set; }
    public string LiveMdPrefix { get; set; } = "live-md";
    public string CursorPrefix { get; set; } = "_canon-cursors";
    public string Venue { get; set; } = "";
    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>Absolute base dir the canonical CSV writers partition under (the writers'
    /// ResumeFrom does Directory.GetFiles, so this must be a real FS path). Defaults to the
    /// storage DataRoot, set during host wiring.</summary>
    public string AssetDirBase { get; set; } = "";

    /// <summary>instrument -> asset dir relative to AssetDirBase (e.g. "BTCUSDT" -> "binance/BTCUSDT_perp").</summary>
    public Dictionary<string, string> InstrumentAssetDirs { get; set; } = new();
}
```

- [ ] **Step 2: Implement the DI extension**

Registers the three typed canonicalizers behind the non-generic `IStreamCanonicalizer` set, the cursor store, the projections, and the map. Adding a future stream type adds one `services.AddSingleton<IStreamCanonicalizer>(...)` line here and a new projection file — nothing else.

```csharp
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Storage;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Canonicalization;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

public static class CanonicalizerServiceCollectionExtensions
{
    public static IServiceCollection AddTickCanonicalizer(this IServiceCollection services)
    {
        services.AddSingleton<ISessionFeedWriter, DailySessionCsvWriter>();

        services.AddSingleton(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<CanonicalizerOptions>>().Value;
            return new InstrumentAssetDirMap(opt.AssetDirBase, opt.InstrumentAssetDirs);
        });

        services.AddSingleton<IStreamCursorStore, FileStreamCursorStore>();

        services.AddSingleton<IStreamProjection<TradeTick>, TradeProjection>();
        services.AddSingleton<IStreamProjection<QuoteTick>, QuoteProjection>();
        services.AddSingleton<IStreamProjection<SessionEvent>, SessionProjection>();

        services.AddSingleton<IStreamCanonicalizer>(sp => Build<TradeTick>(sp));
        services.AddSingleton<IStreamCanonicalizer>(sp => Build<QuoteTick>(sp));
        services.AddSingleton<IStreamCanonicalizer>(sp => Build<SessionEvent>(sp));

        return services;
    }

    private static StreamCanonicalizer<T> Build<T>(IServiceProvider sp) where T : IFramePayload<T>
    {
        var opt = sp.GetRequiredService<IOptions<CanonicalizerOptions>>().Value;
        return new StreamCanonicalizer<T>(
            sp.GetRequiredService<IFileStorage>(),
            sp.GetRequiredService<IStreamProjection<T>>(),
            sp.GetRequiredService<IStreamCursorStore>(),
            opt.LiveMdPrefix,
            opt.CursorPrefix);
    }
}
```

> `DailySessionCsvWriter` is `internal`; the extension lives in the same assembly so it is visible via the `...Infrastructure.Storage` using.

- [ ] **Step 3: Implement `TickCanonicalizerService`** (config-gated; OCE-safe)

```csharp
using AlgoTradeForge.HistoryLoader.Application.Canonicalization;
using AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Collection;

/// <summary>
/// Tails the uploaded live-md/{venue}/ relay prefix and canonicalizes each stream. Idle until a
/// LiveHost producer (Plan 3) uploads segments. Config-gated by Canonicalizer:Enabled.
/// </summary>
internal sealed class TickCanonicalizerService(
    IEnumerable<IStreamCanonicalizer> canonicalizers,
    IFileStorage storage,
    IOptions<CanonicalizerOptions> options,
    ILogger<TickCanonicalizerService> logger) : BackgroundService
{
    private readonly CanonicalizerOptions _options = options.Value;

    private static bool IsTrueShutdown(Exception ex, CancellationToken token) =>
        ex is OperationCanceledException oce && token.IsCancellationRequested && oce.CancellationToken == token;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("TickCanonicalizerService disabled (Canonicalizer:Enabled=false)");
            return;
        }

        var byStream = canonicalizers.ToDictionary(c => c.StreamName, StringComparer.Ordinal);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds)));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
                await CanonicalizeCycle(byStream, stoppingToken);
            }
            catch (Exception ex) when (!IsTrueShutdown(ex, stoppingToken))
            {
                logger.LogError(ex, "Canonicalize cycle failed; will retry next tick");
            }
        }
    }

    private async Task CanonicalizeCycle(
        IReadOnlyDictionary<string, IStreamCanonicalizer> byStream, CancellationToken ct)
    {
        var venuePrefix = $"{_options.LiveMdPrefix}/{_options.Venue}/";
        var seen = new HashSet<(string instrumentOrVenue, string stream)>();

        await foreach (var key in storage.ListKeys(venuePrefix, ".atft", recursive: true, ct))
        {
            if (!SegmentKeyParser.TryParse(key, _options.LiveMdPrefix, out var loc)) continue;
            seen.Add((loc.InstrumentOrVenue, loc.StreamName));
        }

        foreach (var (instrumentOrVenue, stream) in seen)
        {
            ct.ThrowIfCancellationRequested();
            if (!byStream.TryGetValue(stream, out var canon)) continue; // unknown stream type — skip
            var n = await canon.Run(_options.Venue, instrumentOrVenue, ct);
            if (n > 0)
                logger.LogInformation("Canonicalized {Count} {Stream} frames for {Instrument}",
                    n, stream, instrumentOrVenue);
        }
    }
}
```

- [ ] **Step 4: Wire into `Program.cs`**

Find where `AddHistoryLoaderInfrastructure()` is called and where the Storage section binds options. Add after the infrastructure registration:

```csharp
builder.Services.Configure<CanonicalizerOptions>(
    builder.Configuration.GetSection(CanonicalizerOptions.SectionName));
builder.Services.PostConfigure<CanonicalizerOptions>(opt =>
{
    if (string.IsNullOrEmpty(opt.AssetDirBase))
        opt.AssetDirBase = builder.Configuration.GetSection("Storage:Local:DataRoot").Value ?? "";
});
builder.Services.AddTickCanonicalizer();
builder.Services.AddHostedService<TickCanonicalizerService>();
```

Add the required usings at the top of `Program.cs`:

```csharp
using AlgoTradeForge.HistoryLoader.Application.Canonicalization;
using AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;
using AlgoTradeForge.HistoryLoader.WebApi.Collection;
```

> If `Storage:Local:DataRoot` is not the exact config path in this host, use whatever key the Storage section already binds for the local DataRoot — grep `Program.cs` for `DataRoot`. The default-off gate means a wrong base is inert until `Enabled=true`.

- [ ] **Step 5: Write the dispatch test** (the BackgroundService cycle, driven directly)

```csharp
using AlgoTradeForge.Storage;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Canonicalization;
using AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Canonicalization;

public sealed class CanonicalizerDispatchTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileStorage _storage;
    private readonly LocalTailIndex _tail;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly long Ts =
        new DateTimeOffset(2024, 3, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public CanonicalizerDispatchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"CanonDispatch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _root });
        _tail = new LocalTailIndex(_storage);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task ThreeStreams_AllCanonicalize()
    {
        var map = new InstrumentAssetDirMap(_root, new Dictionary<string, string>());
        var cursors = new FileStreamCursorStore(_storage);
        var opts = Options.Create(new HistoryLoaderStorageOptions { FlushEveryRows = 1, FlushIntervalSeconds = 60 });

        var tradeWriter = new DailyTickCsvWriter(_storage, _tail, opts, NullLogger<DailyTickCsvWriter>.Instance, new WriteLockManager());
        var quoteWriter = new DailyBookTickerCsvWriter(_storage, _tail, opts, NullLogger<DailyBookTickerCsvWriter>.Instance, new WriteLockManager());
        var sessionWriter = new DailySessionCsvWriter(_storage, _tail, opts, NullLogger<DailySessionCsvWriter>.Instance, new WriteLockManager());

        IStreamCanonicalizer[] canon =
        [
            new StreamCanonicalizer<TradeTick>(_storage, new TradeProjection(tradeWriter, map), cursors, "live-md", "_canon-cursors"),
            new StreamCanonicalizer<QuoteTick>(_storage, new QuoteProjection(quoteWriter, map), cursors, "live-md", "_canon-cursors"),
            new StreamCanonicalizer<SessionEvent>(_storage, new SessionProjection(sessionWriter, map), cursors, "live-md", "_canon-cursors"),
        ];

        await WriteSegment("BTCUSDT", "trades", new TradeTick(Ts, 5000050, 123, 1, AggressorSide.Buy));
        await WriteSegment("BTCUSDT", "quotes", new QuoteTick(Ts, 5000000, 100, 5000100, 200, 1));
        await WriteSegment("binance", "_session", new SessionEvent(Ts, SessionEventKind.SessionStart));

        var byStream = canon.ToDictionary(c => c.StreamName, StringComparer.Ordinal);
        foreach (var (inst, stream) in new[] { ("BTCUSDT", "trades"), ("BTCUSDT", "quotes"), ("binance", "_session") })
            await byStream[stream].Run("binance", inst, Ct);

        Assert.True(await _storage.Exists(Path.Combine(map.Resolve("binance", "BTCUSDT"), "ticks",
            $"{DateTimeOffset.FromUnixTimeMilliseconds(Ts).UtcDateTime:yyyy-MM-dd}.csv"), Ct));
        Assert.True(await _storage.Exists(Path.Combine(map.Resolve("binance", "BTCUSDT"), "book-ticker",
            $"{DateTimeOffset.FromUnixTimeMilliseconds(Ts).UtcDateTime:yyyy-MM-dd}.csv"), Ct));
        Assert.True(await _storage.Exists(Path.Combine(map.VenueDir("binance"), "_session",
            $"{DateTimeOffset.FromUnixTimeMilliseconds(Ts).UtcDateTime:yyyy-MM-dd}.csv"), Ct));
    }

    private async Task WriteSegment<T>(string instrumentOrVenue, string stream, T frame) where T : IFramePayload<T>
    {
        using var ms = new MemoryStream();
        using (var w = new SegmentWriter<T>(ms,
            new SegmentHeader(2, 3, 0, Ts, 0, (ushort)T.PayloadSize), leaveOpen: true))
            w.Write(frame);
        await _storage.WriteAllBytes($"live-md/binance/{instrumentOrVenue}/{stream}/{Ts:D13}-{0:D19}.atft", ms.ToArray(), Ct);
    }
}
```

- [ ] **Step 6: Build, then run the test**

Run: `dotnet build AlgoTradeForge.slnx`
Expected: PASS.
Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter CanonicalizerDispatchTests`
Expected: PASS (all three feeds materialized).

- [ ] **Step 7: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/Canonicalization/CanonicalizerOptions.cs \
        src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/CanonicalizerServiceCollectionExtensions.cs \
        src/AlgoTradeForge.HistoryLoader.WebApi/Collection/TickCanonicalizerService.cs \
        src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Canonicalization/CanonicalizerDispatchTests.cs
git commit -F - <<'EOF'
feat(canonicalizer): options, DI, and config-gated BackgroundService

CanonicalizerOptions + AddTickCanonicalizer register the three typed
canonicalizers behind IStreamCanonicalizer. TickCanonicalizerService discovers
streams under live-md/{venue}/ and dispatches by stream name; default-off,
OCE-safe loop. Wired into Program.cs.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_018w22NAfM8bQwp5TTiMMGbX
EOF
```

---

## Task 8: Open/closed acceptance test + end-to-end round-trip

**Files:**
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Canonicalization/OpenClosedAcceptanceTests.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Canonicalization/EndToEndRoundTripTests.cs`

**Interfaces:**
- Consumes: everything above. Defines a throwaway `DepthTick` payload + `DepthProjection` **entirely in the test project** — the proof that a new stream type touches zero production tail-loop/cursor/parser files.

- [ ] **Step 1: Write the open/closed acceptance test**

The whole point: `DepthTick` and its projection live in the test assembly; `StreamCanonicalizer<DepthTick>` is constructed with no edits to any production canonicalizer file.

```csharp
using System.Buffers.Binary;
using AlgoTradeForge.Storage;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Canonicalization;

// A brand-new event type defined ONLY in the test project.
public readonly record struct DepthTick(long TimestampMs, long Level, long Price, long Size, long Sequence)
    : IFramePayload<DepthTick>
{
    public static string StreamName => "depth";
    public static int PayloadSize => 40;
    public int WriteTo(Span<byte> d)
    {
        BinaryPrimitives.WriteInt64LittleEndian(d[0..], TimestampMs);
        BinaryPrimitives.WriteInt64LittleEndian(d[8..], Level);
        BinaryPrimitives.WriteInt64LittleEndian(d[16..], Price);
        BinaryPrimitives.WriteInt64LittleEndian(d[24..], Size);
        BinaryPrimitives.WriteInt64LittleEndian(d[32..], Sequence);
        return PayloadSize;
    }
    public static DepthTick ReadFrom(ReadOnlySpan<byte> s) => new(
        BinaryPrimitives.ReadInt64LittleEndian(s[0..]),
        BinaryPrimitives.ReadInt64LittleEndian(s[8..]),
        BinaryPrimitives.ReadInt64LittleEndian(s[16..]),
        BinaryPrimitives.ReadInt64LittleEndian(s[24..]),
        BinaryPrimitives.ReadInt64LittleEndian(s[32..]));
    public string Format() => $"DEPTH ts={TimestampMs} lvl={Level} px={Price} sz={Size} seq={Sequence}";
}

// A throwaway projection that records what it received — no production writer needed.
internal sealed class DepthProjection : IStreamProjection<DepthTick>
{
    public List<DepthTick> Received { get; } = new();
    public void Apply(in DepthTick frame, in SegmentHeader header, SegmentLocation loc) => Received.Add(frame);
    public Task Seed(SegmentLocation loc, CancellationToken ct) => Task.CompletedTask;
    public Task Flush(CancellationToken ct) => Task.CompletedTask;
}

public sealed class OpenClosedAcceptanceTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileStorage _storage;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public OpenClosedAcceptanceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"OpenClosed_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _root });
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task NewStreamType_CanonicalizesWithZeroProductionEdits()
    {
        long ts = 1_700_000_000_000;
        using var ms = new MemoryStream();
        using (var w = new SegmentWriter<DepthTick>(ms,
            new SegmentHeader(2, 3, 0, ts, 0, (ushort)DepthTick.PayloadSize), leaveOpen: true))
            w.Write(new DepthTick(ts, 0, 5000000, 100, 1));
        await _storage.WriteAllBytes($"live-md/binance/BTCUSDT/depth/{ts:D13}-{0:D19}.atft", ms.ToArray(), Ct);

        var proj = new DepthProjection();
        var canon = new StreamCanonicalizer<DepthTick>(
            _storage, proj, new FileStreamCursorStore(_storage), "live-md", "_canon-cursors");

        var n = await canon.Run("binance", "BTCUSDT", Ct);

        Assert.Equal(1, n);
        Assert.Single(proj.Received);
        Assert.Equal("depth", canon.StreamName);
    }
}
```

- [ ] **Step 2: Run it; confirm PASS**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter OpenClosedAcceptanceTests`
Expected: PASS — proves a new stream type required only a new payload + projection, no edits to `StreamCanonicalizer`, `SegmentKeyParser`, or `FileStreamCursorStore`.

- [ ] **Step 3: Write the end-to-end round-trip test** (relay write → canonicalize → backtest-loader-format CSV)

The canonical `ticks/<day>.csv` produced here is byte-for-byte the schema the existing tick consumers read (asserted against the exact format `DailyTickCsvWriterTests` pins). This closes capture → archive → backtest.

```csharp
using AlgoTradeForge.Storage;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Canonicalization;

public sealed class EndToEndRoundTripTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileStorage _storage;
    private readonly LocalTailIndex _tail;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly long Ts =
        new DateTimeOffset(2024, 3, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public EndToEndRoundTripTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"E2E_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _root });
        _tail = new LocalTailIndex(_storage);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task RelayWrite_Canonicalize_ProducesBacktestReadableTickCsv()
    {
        var map = new InstrumentAssetDirMap(_root, new Dictionary<string, string>());
        var writer = new DailyTickCsvWriter(_storage, _tail,
            Options.Create(new HistoryLoaderStorageOptions { FlushEveryRows = 1, FlushIntervalSeconds = 60 }),
            NullLogger<DailyTickCsvWriter>.Instance, new WriteLockManager());
        var canon = new StreamCanonicalizer<TradeTick>(
            _storage, new TradeProjection(writer, map), new FileStreamCursorStore(_storage), "live-md", "_canon-cursors");

        // Relay-side write of two scaled-long trades (price exp 2, qty exp 3).
        using (var ms = new MemoryStream())
        {
            using (var w = new SegmentWriter<TradeTick>(ms,
                new SegmentHeader(2, 3, 0, Ts, 1, (ushort)TradeTick.PayloadSize), leaveOpen: true))
            {
                w.Write(new TradeTick(Ts, 5000050, 123, 1, AggressorSide.Buy));
                w.Write(new TradeTick(Ts + 1000, 5000100, 250, 2, AggressorSide.Sell));
            }
            await _storage.WriteAllBytes($"live-md/binance/BTCUSDT/trades/{Ts:D13}-{1:D19}.atft", ms.ToArray(), Ct);
        }

        await canon.Run("binance", "BTCUSDT", Ct);

        var key = Path.Combine(map.Resolve("binance", "BTCUSDT"), "ticks",
            $"{DateTimeOffset.FromUnixTimeMilliseconds(Ts).UtcDateTime:yyyy-MM-dd}.csv");
        var lines = await _storage.ReadAllLines(key, Ct);

        Assert.Equal("ts,price,qty,is_buyer_maker,agg_id", lines[0]);
        Assert.Equal($"{Ts},50000.5,0.123,0,1", lines[1]);
        Assert.Equal($"{Ts + 1000},50001,0.25,1,2", lines[2]);
    }
}
```

- [ ] **Step 4: Run the full HistoryLoader test suite**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/`
Expected: PASS (all new + all pre-existing).

- [ ] **Step 5: Commit**

```bash
git add tests/AlgoTradeForge.HistoryLoader.Tests/Canonicalization/OpenClosedAcceptanceTests.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Canonicalization/EndToEndRoundTripTests.cs
git commit -F - <<'EOF'
test(canonicalizer): open/closed acceptance + end-to-end round-trip

DepthTick (test-only) canonicalizes via StreamCanonicalizer<DepthTick> with zero
edits to any production tail-loop/cursor/parser file — the consumer-side
open/closed proof. End-to-end: relay scaled-long write -> canonicalize ->
backtest-readable ticks CSV, closing capture->archive->backtest.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_018w22NAfM8bQwp5TTiMMGbX
EOF
```

---

## Final verification (whole branch)

- [ ] `dotnet build AlgoTradeForge.slnx` — clean.
- [ ] `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/` — green.
- [ ] `dotnet test tests/AlgoTradeForge.Domain.Tests/` and the relay tests — confirm no regression in shared `Domain.History` / `Live.Relay`.
- [ ] Whole-branch review (opus) against the design doc: open/closed seam intact, two-layer idempotency correct, no per-frame boxing, BG-service OCE-safe, no `Async` suffixes, one-type-per-file.

## Self-review notes (author)

- **Spec coverage:** trades+quotes+_session scope → Tasks 3,4,7,8; `_session` per-venue CSV + ts-watermark → Task 3; library + BackgroundService → Tasks 6,7; un-scale from header (retires (2,0) debt) → Task 2; two-layer cursor+CAS idempotency → Tasks 5,6; instrument→assetDir map → Task 4; open/closed acceptance + e2e → Task 8. All design sections map to a task.
- **Type consistency:** `IStreamProjection<T>.{Apply,Seed,Flush}`, `IStreamCanonicalizer.{StreamName,Run}`, `IStreamCursorStore.{Read,Advance}`, `StreamCursor(LastSegmentKey,ETag)`, `CanonicalScale.{Unscale,ToIsBuyerMaker}`, `SegmentKeyParser.TryParse`, `InstrumentAssetDirMap.{Resolve,VenueDir}`, `CanonicalizerOptions` property names — all referenced consistently across Tasks 1–8.
- **Known soft spot:** the `Program.cs` `DataRoot` config key (Task 7 Step 4) is host-specific; the default-off gate makes a wrong guess inert. The implementer greps `Program.cs` to confirm the exact Storage section key.
