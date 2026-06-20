# LiveHost Binary Tick Relay + GC-Free Ingest→Archival Pipeline — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Post-implementation amendment (2026-06-20):** This plan was executed with the type named `Tick` and a `TickFlags { None, BuyerMaker, Bid, Ask }` enum (as written in the task bodies below). After implementation, the canonical type was **renamed for asset-class uniformity** (see the design spec's "Canonical tick model" section): `Tick` → **`TradeTick`** (it models a single executed trade print), and `TickFlags` → **`AggressorSide { Unknown, Buy, Sell }`** (asset-neutral; crypto `is_buyer_maker` maps in, `Bid`/`Ask` removed — those belong to the future `QuoteTick`/BBO type). `RelayFrame.Tick` became `RelayFrame.Trade`. The 40-byte wire layout is **byte-identical** — frame byte 1 simply carries an `AggressorSide` code instead of a flags byte. **Read every `Tick`/`TickFlags`/`BuyerMaker` in the task bodies below as `TradeTick`/`AggressorSide`/`AggressorSide.Sell` respectively.** A future plan adds `QuoteTick` for the `bookTicker` feed using the same framing.

**Goal:** Build the foundation layer of LiveHost's single-session-venue tick capture — a binary framed append-log format, a GC-free writer/reader, a multi-instrument relay writer with rotation/fsync/heartbeat/backpressure, a local-disk sink + S3 uploader, a `dump-ticks` CLI, and a synthetic 1000-instrument firehose benchmark — fully buildable and testable against the codebase as it exists today, with no dependency on the (not-yet-extracted) LiveHost host.

**Architecture:** Normalized ticks (`Tick` struct, scaled-long price/qty per the Int64 money convention) are enqueued into a bounded channel; a single drain task serializes them as fixed-width 40-byte little-endian frames into per-instrument local segment files (the disk-backed spill), interleaving periodic heartbeat and session-boundary marker frames so the downstream HistoryLoader canonicalizer (a later plan) can distinguish "producer down" from "market quiet." Completed segments are swept to S3 by a separate uploader, so a stalled push backs up only disk, never capture. Losslessness is a durability property (append → `fsync` on rotation → bounded channel with the local file as spill), independent of format.

**Tech Stack:** C# 14 / .NET 10; `System.Buffers.Binary.BinaryPrimitives` for zero-alloc encode/decode; `System.Threading.Channels` (bounded); `System.IO.FileStream.Flush(flushToDisk: true)` for durability; xUnit v3 for tests; BenchmarkDotNet (`[MemoryDiagnoser]` + existing `BriefJsonConfig`) for the firehose measurement; `IFileStorage` (`AlgoTradeForge.Storage.Abstractions`) for upload.

## Global Constraints

- **Target framework `net10.0`, `LangVersion 14`, `Nullable enable`, `ImplicitUsings enable`** on every new project (mirror existing csproj).
- **One dotnet process at a time** — never run build/test in parallel; wait for each to finish.
- **Int64 money convention:** `Tick.Price` and `Tick.Quantity` are scaled `long`s; the segment header records the scale exponents (`PriceScaleExp`, `QtyScaleExp`) so a reader/canonicalizer can reconstruct exact venue values. No raw `(long)` casts of monetary `decimal` (not applicable here — all values are already `long`).
- **Comment convention:** prefer no comments; terse single-line only for a non-obvious formula/layout/pitfall. No signature restatement.
- **File organization:** one public type per file, named after the type. `[Flags]` enum + the struct it accompanies may share a file only if the enum is a single-line companion; otherwise separate files.
- **Async I/O convention:** I/O-fronting methods are `Task`/`ValueTask` with `CancellationToken ct = default`; **no `Async` suffix**. No sync-over-async at production call sites.
- **Resource release:** prefer `using var` over `try`/`finally` when the `finally` is purely a release.
- **xUnit analyzers:** `Assert.Single`/`Assert.Empty`, not `Assert.Equal(1, …)`/`Assert.Equal(0, …)`.
- **All binary encoding is little-endian.**
- **Do NOT `git add`/stage** — leave edits unstaged for the owner to review (each "Commit" step below is written for the owner to run, or for an executing agent only if the owner has authorized commits for this plan).

## File Structure

```
src/
  AlgoTradeForge.Domain/History/
    Tick.cs                         # readonly record struct Tick (new)
    TickFlags.cs                    # [Flags] enum TickFlags : byte (new)
  AlgoTradeForge.Live.Relay/        # NEW class library
    AlgoTradeForge.Live.Relay.csproj
    FrameType.cs                    # enum FrameType : byte
    SessionBoundaryReason.cs        # enum SessionBoundaryReason : byte
    RelayFormat.cs                  # magic/size/version constants
    RelayFrame.cs                   # readonly record struct RelayFrame
    TickSegmentHeader.cs            # header struct + WriteTo/ReadFrom
    TickSegmentWriter.cs            # GC-free frame serializer
    TickSegmentReader.cs            # frame parser
    RelayFrameFormatter.cs          # frame → text (for dump-ticks + tests)
    ITickSegmentSink.cs             # segment lifecycle abstraction
    LocalFileSegmentSink.cs         # per-instrument local .atft files (the spill)
    TickRelayOptions.cs             # capacity / rotation / heartbeat config
    TickRelayWriter.cs              # bounded channel + drain + rotation + heartbeat
    SegmentUploader.cs              # sweeps completed local segments → IFileStorage
  AlgoTradeForge.DumpTicks/         # NEW console tool
    AlgoTradeForge.DumpTicks.csproj
    Program.cs
tests/
  AlgoTradeForge.Live.Relay.Tests/  # NEW xUnit v3 project
    AlgoTradeForge.Live.Relay.Tests.csproj
    TickSegmentHeaderTests.cs
    TickSegmentRoundTripTests.cs
    RelayFrameFormatterTests.cs
    TickRelayWriterTests.cs
    SegmentUploaderTests.cs
  AlgoTradeForge.Domain.Tests/History/
    TickTests.cs                    # add to existing project
benchmarks/AlgoTradeForge.Benchmarks/Benchmarks/
    TickRelayBenchmarks.cs          # add to existing project
AlgoTradeForge.slnx                 # add the 3 new projects
```

**Binary layout (authoritative — every task depends on these exact offsets):**

Segment header — **64 bytes**, little-endian:

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | 4 | Magic | ASCII `ATFT` (`"ATFT"u8`) |
| 4 | 2 | FormatVersion (u16) | `1` |
| 6 | 2 | FrameSize (u16) | `40` |
| 8 | 1 | PriceScaleExp (i8) | `price_real = Price / 10^PriceScaleExp` |
| 9 | 1 | QtyScaleExp (i8) | `qty_real = Quantity / 10^QtyScaleExp` |
| 10 | 6 | reserved | zero |
| 16 | 8 | EpochBaseMs (i64) | `0` = absolute timestamps |
| 24 | 8 | CreatedAtMs (i64) | wall clock at segment open |
| 32 | 8 | FirstSequence (i64) | first tick sequence in segment |
| 40 | 24 | reserved | zero |

Frame — **40 bytes**, little-endian:

| Offset | Size | Field | Tick | Heartbeat | SessionBoundary |
|---|---|---|---|---|---|
| 0 | 1 | FrameType (u8) | `1` | `2` | `3` |
| 1 | 1 | Flags / Reason (u8) | `TickFlags` | `0` | `SessionBoundaryReason` |
| 2 | 6 | reserved | zero | zero | zero |
| 8 | 8 | TimestampMs (i64) | event ts | wall clock | wall clock |
| 16 | 8 | Price (i64) | scaled | `0` | `0` |
| 24 | 8 | Quantity (i64) | scaled | `0` | `0` |
| 32 | 8 | Sequence (i64) | per-instrument monotonic (dedup/gap = `agg_id` role) | `0` | `0` |

---

### Task 1: `Tick` value type in Domain

**Files:**
- Create: `src/AlgoTradeForge.Domain/History/TickFlags.cs`
- Create: `src/AlgoTradeForge.Domain/History/Tick.cs`
- Test: `tests/AlgoTradeForge.Domain.Tests/History/TickTests.cs`

**Interfaces:**
- Produces: `enum TickFlags : byte { None=0, BuyerMaker=1, Bid=2, Ask=4 }`; `readonly record struct Tick(long TimestampMs, long Price, long Quantity, long Sequence, TickFlags Flags)` with `DateTimeOffset Timestamp` computed property. Consumed by every later task and by the future strategy `OnTick` path.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/AlgoTradeForge.Domain.Tests/History/TickTests.cs
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Tests.History;

public class TickTests
{
    [Fact]
    public void Timestamp_ConvertsUnixMillisToDateTimeOffset()
    {
        var tick = new Tick(1_700_000_000_000, 5_000_000, 1_250, 42, TickFlags.BuyerMaker);

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000), tick.Timestamp);
        Assert.True(tick.Flags.HasFlag(TickFlags.BuyerMaker));
    }

    [Fact]
    public void Equality_IsValueBased()
    {
        var a = new Tick(1, 2, 3, 4, TickFlags.Ask);
        var b = new Tick(1, 2, 3, 4, TickFlags.Ask);

        Assert.Equal(a, b);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.Domain.Tests/ --filter FullyQualifiedName~TickTests`
Expected: FAIL — `Tick`/`TickFlags` do not exist (compile error).

- [ ] **Step 3: Write the types**

```csharp
// src/AlgoTradeForge.Domain/History/TickFlags.cs
namespace AlgoTradeForge.Domain.History;

[Flags]
public enum TickFlags : byte
{
    None = 0,
    BuyerMaker = 1,
    Bid = 2,
    Ask = 4,
}
```

```csharp
// src/AlgoTradeForge.Domain/History/Tick.cs
namespace AlgoTradeForge.Domain.History;

public readonly record struct Tick(
    long TimestampMs,
    long Price,
    long Quantity,
    long Sequence,
    TickFlags Flags)
{
    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.Domain.Tests/ --filter FullyQualifiedName~TickTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.Domain/History/Tick.cs src/AlgoTradeForge.Domain/History/TickFlags.cs tests/AlgoTradeForge.Domain.Tests/History/TickTests.cs
git commit -m "feat(domain): add Tick value type for live market data"
```

---

### Task 2: Scaffold relay library + test project; frame/marker enums + format constants

**Files:**
- Create: `src/AlgoTradeForge.Live.Relay/AlgoTradeForge.Live.Relay.csproj`
- Create: `src/AlgoTradeForge.Live.Relay/FrameType.cs`
- Create: `src/AlgoTradeForge.Live.Relay/SessionBoundaryReason.cs`
- Create: `src/AlgoTradeForge.Live.Relay/RelayFormat.cs`
- Create: `src/AlgoTradeForge.Live.Relay/RelayFrame.cs`
- Create: `tests/AlgoTradeForge.Live.Relay.Tests/AlgoTradeForge.Live.Relay.Tests.csproj`
- Modify: `AlgoTradeForge.slnx`
- Test: `tests/AlgoTradeForge.Live.Relay.Tests/RelayFormatTests.cs`

**Interfaces:**
- Consumes: `Tick`, `TickFlags` (Task 1).
- Produces: `enum FrameType : byte { Tick=1, Heartbeat=2, SessionBoundary=3 }`; `enum SessionBoundaryReason : byte { SessionStart=1, SessionEnd=2, ConnectorRestart=3 }`; `static class RelayFormat { const int HeaderSize=64; const int FrameSize=40; const ushort CurrentVersion=1; static ReadOnlySpan<byte> Magic }`; `readonly record struct RelayFrame(FrameType Type, long TimestampMs, Tick Tick, byte ReasonCode)`.

- [ ] **Step 1: Create the library csproj**

```xml
<!-- src/AlgoTradeForge.Live.Relay/AlgoTradeForge.Live.Relay.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\AlgoTradeForge.Domain\AlgoTradeForge.Domain.csproj" />
    <ProjectReference Include="..\AlgoTradeForge.Storage.Abstractions\AlgoTradeForge.Storage.Abstractions.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create the format types**

```csharp
// src/AlgoTradeForge.Live.Relay/FrameType.cs
namespace AlgoTradeForge.Live.Relay;

public enum FrameType : byte
{
    Tick = 1,
    Heartbeat = 2,
    SessionBoundary = 3,
}
```

```csharp
// src/AlgoTradeForge.Live.Relay/SessionBoundaryReason.cs
namespace AlgoTradeForge.Live.Relay;

public enum SessionBoundaryReason : byte
{
    SessionStart = 1,
    SessionEnd = 2,
    ConnectorRestart = 3,
}
```

```csharp
// src/AlgoTradeForge.Live.Relay/RelayFormat.cs
namespace AlgoTradeForge.Live.Relay;

public static class RelayFormat
{
    public const int HeaderSize = 64;
    public const int FrameSize = 40;
    public const ushort CurrentVersion = 1;

    public static ReadOnlySpan<byte> Magic => "ATFT"u8;
}
```

```csharp
// src/AlgoTradeForge.Live.Relay/RelayFrame.cs
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Live.Relay;

public readonly record struct RelayFrame(FrameType Type, long TimestampMs, Tick Tick, byte ReasonCode);
```

- [ ] **Step 3: Create the test csproj**

```xml
<!-- tests/AlgoTradeForge.Live.Relay.Tests/AlgoTradeForge.Live.Relay.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="NSubstitute" Version="5.3.0" />
    <PackageReference Include="xunit.v3" Version="3.2.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\AlgoTradeForge.Live.Relay\AlgoTradeForge.Live.Relay.csproj" />
    <ProjectReference Include="..\..\src\AlgoTradeForge.Domain\AlgoTradeForge.Domain.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 4: Register all three projects in the solution**

Add these lines inside `<Solution>` in `AlgoTradeForge.slnx` (the library next to the other `src` projects; the test project inside the `/tests/` folder):

```xml
  <Project Path="src\AlgoTradeForge.Live.Relay\AlgoTradeForge.Live.Relay.csproj" />
  <Project Path="src\AlgoTradeForge.DumpTicks\AlgoTradeForge.DumpTicks.csproj" />
  <Project Path="tests\AlgoTradeForge.Live.Relay.Tests\AlgoTradeForge.Live.Relay.Tests.csproj" />
```

(The `AlgoTradeForge.DumpTicks` project is created in Task 6; registering it now is harmless only if the file exists — if the build fails on a missing project, add this line in Task 6 instead. Prefer adding the DumpTicks line in Task 6 to keep this task green.)

- [ ] **Step 5: Write the failing test**

```csharp
// tests/AlgoTradeForge.Live.Relay.Tests/RelayFormatTests.cs
using AlgoTradeForge.Live.Relay;

namespace AlgoTradeForge.Live.Relay.Tests;

public class RelayFormatTests
{
    [Fact]
    public void Constants_MatchWireSpec()
    {
        Assert.Equal(64, RelayFormat.HeaderSize);
        Assert.Equal(40, RelayFormat.FrameSize);
        Assert.Equal(1, RelayFormat.CurrentVersion);
        Assert.True(RelayFormat.Magic.SequenceEqual("ATFT"u8));
    }
}
```

- [ ] **Step 6: Build + run test**

Run: `dotnet test tests/AlgoTradeForge.Live.Relay.Tests/`
Expected: PASS (1 test); solution builds with the new library + test project.

- [ ] **Step 7: Commit**

```bash
git add src/AlgoTradeForge.Live.Relay tests/AlgoTradeForge.Live.Relay.Tests AlgoTradeForge.slnx
git commit -m "feat(relay): scaffold Live.Relay library + frame format constants"
```

---

### Task 3: `TickSegmentHeader` — binary read/write round-trip

**Files:**
- Create: `src/AlgoTradeForge.Live.Relay/TickSegmentHeader.cs`
- Test: `tests/AlgoTradeForge.Live.Relay.Tests/TickSegmentHeaderTests.cs`

**Interfaces:**
- Consumes: `RelayFormat`.
- Produces: `readonly record struct TickSegmentHeader(sbyte PriceScaleExp, sbyte QtyScaleExp, long EpochBaseMs, long CreatedAtMs, long FirstSequence)` with `void WriteTo(Span<byte>)` and `static TickSegmentHeader ReadFrom(ReadOnlySpan<byte>)`. `ReadFrom` throws `InvalidDataException` on bad magic / unsupported version / wrong frame size.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/AlgoTradeForge.Live.Relay.Tests/TickSegmentHeaderTests.cs
using AlgoTradeForge.Live.Relay;

namespace AlgoTradeForge.Live.Relay.Tests;

public class TickSegmentHeaderTests
{
    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        var header = new TickSegmentHeader(
            PriceScaleExp: 2, QtyScaleExp: 0,
            EpochBaseMs: 0, CreatedAtMs: 1_700_000_000_000, FirstSequence: 99);

        Span<byte> buf = stackalloc byte[RelayFormat.HeaderSize];
        header.WriteTo(buf);

        Assert.True(buf[..4].SequenceEqual("ATFT"u8));
        Assert.Equal(header, TickSegmentHeader.ReadFrom(buf));
    }

    [Fact]
    public void ReadFrom_BadMagic_Throws()
    {
        Span<byte> buf = stackalloc byte[RelayFormat.HeaderSize];
        buf.Fill(0);

        try
        {
            TickSegmentHeader.ReadFrom(buf);
            Assert.Fail("Expected InvalidDataException");
        }
        catch (InvalidDataException) { }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.Live.Relay.Tests/ --filter FullyQualifiedName~TickSegmentHeaderTests`
Expected: FAIL — `TickSegmentHeader` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
// src/AlgoTradeForge.Live.Relay/TickSegmentHeader.cs
using System.Buffers.Binary;

namespace AlgoTradeForge.Live.Relay;

public readonly record struct TickSegmentHeader(
    sbyte PriceScaleExp,
    sbyte QtyScaleExp,
    long EpochBaseMs,
    long CreatedAtMs,
    long FirstSequence)
{
    public void WriteTo(Span<byte> dest)
    {
        if (dest.Length < RelayFormat.HeaderSize)
            throw new ArgumentException($"Header buffer must be >= {RelayFormat.HeaderSize} bytes.", nameof(dest));

        dest[..RelayFormat.HeaderSize].Clear();
        RelayFormat.Magic.CopyTo(dest);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[4..], RelayFormat.CurrentVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[6..], RelayFormat.FrameSize);
        dest[8] = (byte)PriceScaleExp;
        dest[9] = (byte)QtyScaleExp;
        BinaryPrimitives.WriteInt64LittleEndian(dest[16..], EpochBaseMs);
        BinaryPrimitives.WriteInt64LittleEndian(dest[24..], CreatedAtMs);
        BinaryPrimitives.WriteInt64LittleEndian(dest[32..], FirstSequence);
    }

    public static TickSegmentHeader ReadFrom(ReadOnlySpan<byte> src)
    {
        if (src.Length < RelayFormat.HeaderSize)
            throw new ArgumentException($"Header buffer must be >= {RelayFormat.HeaderSize} bytes.", nameof(src));
        if (!src[..4].SequenceEqual(RelayFormat.Magic))
            throw new InvalidDataException("Not an ATFT tick segment (bad magic).");

        var version = BinaryPrimitives.ReadUInt16LittleEndian(src[4..]);
        if (version != RelayFormat.CurrentVersion)
            throw new InvalidDataException($"Unsupported ATFT version {version}.");

        var frameSize = BinaryPrimitives.ReadUInt16LittleEndian(src[6..]);
        if (frameSize != RelayFormat.FrameSize)
            throw new InvalidDataException($"Unexpected frame size {frameSize}.");

        return new TickSegmentHeader(
            PriceScaleExp: (sbyte)src[8],
            QtyScaleExp: (sbyte)src[9],
            EpochBaseMs: BinaryPrimitives.ReadInt64LittleEndian(src[16..]),
            CreatedAtMs: BinaryPrimitives.ReadInt64LittleEndian(src[24..]),
            FirstSequence: BinaryPrimitives.ReadInt64LittleEndian(src[32..]));
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.Live.Relay.Tests/ --filter FullyQualifiedName~TickSegmentHeaderTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.Live.Relay/TickSegmentHeader.cs tests/AlgoTradeForge.Live.Relay.Tests/TickSegmentHeaderTests.cs
git commit -m "feat(relay): tick segment header binary codec"
```

---

### Task 4: `TickSegmentWriter` — GC-free frame serializer

**Files:**
- Create: `src/AlgoTradeForge.Live.Relay/TickSegmentWriter.cs`
- Test: covered by the round-trip test in Task 5 (writer has no independent observable output without the reader; do not write byte-level assertions that duplicate the layout table).

**Interfaces:**
- Consumes: `Tick`, `TickSegmentHeader`, `RelayFormat`, `SessionBoundaryReason`.
- Produces: `sealed class TickSegmentWriter : IDisposable` with ctor `(Stream destination, in TickSegmentHeader header, bool leaveOpen = false)` (writes the header immediately), `void WriteTick(in Tick)`, `void WriteHeartbeat(long timestampMs)`, `void WriteSessionBoundary(long timestampMs, SessionBoundaryReason reason)`, `void Flush(bool toDisk)`. Reuses a single `byte[40]` buffer — **zero allocation per frame**.

- [ ] **Step 1: Write the implementation**

```csharp
// src/AlgoTradeForge.Live.Relay/TickSegmentWriter.cs
using System.Buffers.Binary;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Live.Relay;

public sealed class TickSegmentWriter : IDisposable
{
    private readonly Stream _dest;
    private readonly bool _leaveOpen;
    private readonly byte[] _frame = new byte[RelayFormat.FrameSize];
    private bool _disposed;

    public TickSegmentWriter(Stream destination, in TickSegmentHeader header, bool leaveOpen = false)
    {
        _dest = destination;
        _leaveOpen = leaveOpen;

        Span<byte> hbuf = stackalloc byte[RelayFormat.HeaderSize];
        header.WriteTo(hbuf);
        _dest.Write(hbuf);
    }

    public void WriteTick(in Tick tick)
    {
        var b = _frame.AsSpan();
        b.Clear();
        b[0] = (byte)FrameType.Tick;
        b[1] = (byte)tick.Flags;
        BinaryPrimitives.WriteInt64LittleEndian(b[8..], tick.TimestampMs);
        BinaryPrimitives.WriteInt64LittleEndian(b[16..], tick.Price);
        BinaryPrimitives.WriteInt64LittleEndian(b[24..], tick.Quantity);
        BinaryPrimitives.WriteInt64LittleEndian(b[32..], tick.Sequence);
        _dest.Write(_frame, 0, RelayFormat.FrameSize);
    }

    public void WriteHeartbeat(long timestampMs) => WriteMarker(FrameType.Heartbeat, timestampMs, 0);

    public void WriteSessionBoundary(long timestampMs, SessionBoundaryReason reason) =>
        WriteMarker(FrameType.SessionBoundary, timestampMs, (byte)reason);

    private void WriteMarker(FrameType type, long timestampMs, byte reason)
    {
        var b = _frame.AsSpan();
        b.Clear();
        b[0] = (byte)type;
        b[1] = reason;
        BinaryPrimitives.WriteInt64LittleEndian(b[8..], timestampMs);
        _dest.Write(_frame, 0, RelayFormat.FrameSize);
    }

    public void Flush(bool toDisk)
    {
        if (toDisk && _dest is FileStream fs) fs.Flush(flushToDisk: true);
        else _dest.Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dest.Flush();
        if (!_leaveOpen) _dest.Dispose();
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/AlgoTradeForge.Live.Relay/`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/AlgoTradeForge.Live.Relay/TickSegmentWriter.cs
git commit -m "feat(relay): GC-free tick segment writer"
```

---

### Task 5: `TickSegmentReader` + writer→reader lossless round-trip

**Files:**
- Create: `src/AlgoTradeForge.Live.Relay/TickSegmentReader.cs`
- Test: `tests/AlgoTradeForge.Live.Relay.Tests/TickSegmentRoundTripTests.cs`

**Interfaces:**
- Consumes: `TickSegmentWriter`, `TickSegmentHeader`, `RelayFrame`, `RelayFormat`, `FrameType`.
- Produces: `sealed class TickSegmentReader : IDisposable` with ctor `(Stream source, bool leaveOpen = false)` (reads + validates header), `TickSegmentHeader Header { get; }`, `bool TryReadFrame(out RelayFrame frame)` — returns `false` at clean EOF, throws `EndOfStreamException` on a torn (partial) frame.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/AlgoTradeForge.Live.Relay.Tests/TickSegmentRoundTripTests.cs
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;

namespace AlgoTradeForge.Live.Relay.Tests;

public class TickSegmentRoundTripTests
{
    [Fact]
    public void TicksAndMarkers_SurviveWriteReadCycle()
    {
        var header = new TickSegmentHeader(2, 0, 0, 1_700_000_000_000, 1);
        var ticks = new[]
        {
            new Tick(1_700_000_000_001, 5_000_000, 10, 1, TickFlags.BuyerMaker),
            new Tick(1_700_000_000_002, 5_000_050, 20, 2, TickFlags.None),
            new Tick(1_700_000_000_005, 4_999_900, 7,  3, TickFlags.Ask),
        };

        using var ms = new MemoryStream();
        using (var writer = new TickSegmentWriter(ms, header, leaveOpen: true))
        {
            writer.WriteSessionBoundary(1_700_000_000_000, SessionBoundaryReason.SessionStart);
            foreach (var t in ticks) writer.WriteTick(t);
            writer.WriteHeartbeat(1_700_000_000_010);
            writer.WriteSessionBoundary(1_700_000_000_020, SessionBoundaryReason.SessionEnd);
        }

        ms.Position = 0;
        using var reader = new TickSegmentReader(ms);

        Assert.Equal(header, reader.Header);

        Assert.True(reader.TryReadFrame(out var f0));
        Assert.Equal(FrameType.SessionBoundary, f0.Type);
        Assert.Equal((byte)SessionBoundaryReason.SessionStart, f0.ReasonCode);

        foreach (var expected in ticks)
        {
            Assert.True(reader.TryReadFrame(out var f));
            Assert.Equal(FrameType.Tick, f.Type);
            Assert.Equal(expected, f.Tick);
        }

        Assert.True(reader.TryReadFrame(out var hb));
        Assert.Equal(FrameType.Heartbeat, hb.Type);
        Assert.Equal(1_700_000_000_010, hb.TimestampMs);

        Assert.True(reader.TryReadFrame(out var end));
        Assert.Equal(FrameType.SessionBoundary, end.Type);
        Assert.Equal((byte)SessionBoundaryReason.SessionEnd, end.ReasonCode);

        Assert.False(reader.TryReadFrame(out _));
    }

    [Fact]
    public void TornFrame_Throws()
    {
        var header = new TickSegmentHeader(2, 0, 0, 1, 1);
        using var ms = new MemoryStream();
        using (var writer = new TickSegmentWriter(ms, header, leaveOpen: true))
            writer.WriteTick(new Tick(1, 2, 3, 4, TickFlags.None));

        ms.SetLength(ms.Length - 3); // truncate the last frame
        ms.Position = 0;

        using var reader = new TickSegmentReader(ms);
        Assert.Throws<EndOfStreamException>(() => reader.TryReadFrame(out _));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.Live.Relay.Tests/ --filter FullyQualifiedName~TickSegmentRoundTripTests`
Expected: FAIL — `TickSegmentReader` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
// src/AlgoTradeForge.Live.Relay/TickSegmentReader.cs
using System.Buffers.Binary;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Live.Relay;

public sealed class TickSegmentReader : IDisposable
{
    private readonly Stream _src;
    private readonly bool _leaveOpen;
    private readonly byte[] _frame = new byte[RelayFormat.FrameSize];

    public TickSegmentHeader Header { get; }

    public TickSegmentReader(Stream source, bool leaveOpen = false)
    {
        _src = source;
        _leaveOpen = leaveOpen;

        Span<byte> hbuf = stackalloc byte[RelayFormat.HeaderSize];
        _src.ReadExactly(hbuf);
        Header = TickSegmentHeader.ReadFrom(hbuf);
    }

    public bool TryReadFrame(out RelayFrame frame)
    {
        int n = _src.ReadAtLeast(_frame, RelayFormat.FrameSize, throwOnEndOfStream: false);
        if (n == 0) { frame = default; return false; }
        if (n < RelayFormat.FrameSize) throw new EndOfStreamException("Torn relay frame.");

        var b = _frame.AsSpan();
        var type = (FrameType)b[0];
        byte reason = b[1];
        long ts = BinaryPrimitives.ReadInt64LittleEndian(b[8..]);

        if (type == FrameType.Tick)
        {
            var tick = new Tick(
                ts,
                BinaryPrimitives.ReadInt64LittleEndian(b[16..]),
                BinaryPrimitives.ReadInt64LittleEndian(b[24..]),
                BinaryPrimitives.ReadInt64LittleEndian(b[32..]),
                (TickFlags)reason);
            frame = new RelayFrame(type, ts, tick, 0);
        }
        else
        {
            frame = new RelayFrame(type, ts, default, reason);
        }
        return true;
    }

    public void Dispose()
    {
        if (!_leaveOpen) _src.Dispose();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.Live.Relay.Tests/ --filter FullyQualifiedName~TickSegmentRoundTripTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.Live.Relay/TickSegmentReader.cs tests/AlgoTradeForge.Live.Relay.Tests/TickSegmentRoundTripTests.cs
git commit -m "feat(relay): tick segment reader + lossless round-trip"
```

---

### Task 6: `RelayFrameFormatter` + `dump-ticks` CLI

**Files:**
- Create: `src/AlgoTradeForge.Live.Relay/RelayFrameFormatter.cs`
- Create: `src/AlgoTradeForge.DumpTicks/AlgoTradeForge.DumpTicks.csproj`
- Create: `src/AlgoTradeForge.DumpTicks/Program.cs`
- Modify: `AlgoTradeForge.slnx` (add the DumpTicks project line if not already added in Task 2)
- Test: `tests/AlgoTradeForge.Live.Relay.Tests/RelayFrameFormatterTests.cs`

**Interfaces:**
- Consumes: `RelayFrame`, `FrameType`, `SessionBoundaryReason`, `TickSegmentReader`.
- Produces: `static class RelayFrameFormatter { static string Format(in RelayFrame frame) }` — one line per frame, culture-invariant, restores human-inspection of the binary log.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/AlgoTradeForge.Live.Relay.Tests/RelayFrameFormatterTests.cs
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;

namespace AlgoTradeForge.Live.Relay.Tests;

public class RelayFrameFormatterTests
{
    [Fact]
    public void Format_Tick_IncludesFieldsAndFlags()
    {
        var frame = new RelayFrame(FrameType.Tick, 1_700_000_000_001,
            new Tick(1_700_000_000_001, 5_000_000, 10, 7, TickFlags.BuyerMaker), 0);

        var line = RelayFrameFormatter.Format(frame);

        Assert.Contains("TICK", line);
        Assert.Contains("seq=7", line);
        Assert.Contains("price=5000000", line);
        Assert.Contains("BuyerMaker", line);
    }

    [Fact]
    public void Format_SessionBoundary_NamesReason()
    {
        var frame = new RelayFrame(FrameType.SessionBoundary, 1_700_000_000_020,
            default, (byte)SessionBoundaryReason.SessionEnd);

        var line = RelayFrameFormatter.Format(frame);

        Assert.Contains("BOUNDARY", line);
        Assert.Contains("SessionEnd", line);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.Live.Relay.Tests/ --filter FullyQualifiedName~RelayFrameFormatterTests`
Expected: FAIL — `RelayFrameFormatter` does not exist.

- [ ] **Step 3: Write the formatter**

```csharp
// src/AlgoTradeForge.Live.Relay/RelayFrameFormatter.cs
using System.Globalization;

namespace AlgoTradeForge.Live.Relay;

public static class RelayFrameFormatter
{
    public static string Format(in RelayFrame frame)
    {
        var ci = CultureInfo.InvariantCulture;
        return frame.Type switch
        {
            FrameType.Tick =>
                $"TICK ts={frame.Tick.TimestampMs.ToString(ci)} " +
                $"price={frame.Tick.Price.ToString(ci)} " +
                $"qty={frame.Tick.Quantity.ToString(ci)} " +
                $"seq={frame.Tick.Sequence.ToString(ci)} " +
                $"flags={frame.Tick.Flags}",
            FrameType.Heartbeat =>
                $"HEARTBEAT ts={frame.TimestampMs.ToString(ci)}",
            FrameType.SessionBoundary =>
                $"BOUNDARY ts={frame.TimestampMs.ToString(ci)} reason={(SessionBoundaryReason)frame.ReasonCode}",
            _ => $"UNKNOWN type={(byte)frame.Type}",
        };
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.Live.Relay.Tests/ --filter FullyQualifiedName~RelayFrameFormatterTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Create the CLI project**

```xml
<!-- src/AlgoTradeForge.DumpTicks/AlgoTradeForge.DumpTicks.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>dump-ticks</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\AlgoTradeForge.Live.Relay\AlgoTradeForge.Live.Relay.csproj" />
  </ItemGroup>

</Project>
```

```csharp
// src/AlgoTradeForge.DumpTicks/Program.cs
using AlgoTradeForge.Live.Relay;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: dump-ticks <segment.atft>");
    return 1;
}

using var file = File.OpenRead(args[0]);
using var reader = new TickSegmentReader(file);

var h = reader.Header;
Console.WriteLine($"HEADER priceScaleExp={h.PriceScaleExp} qtyScaleExp={h.QtyScaleExp} " +
                  $"createdAtMs={h.CreatedAtMs} firstSeq={h.FirstSequence}");

long count = 0;
while (reader.TryReadFrame(out var frame))
{
    Console.WriteLine(RelayFrameFormatter.Format(frame));
    count++;
}
Console.WriteLine($"# {count} frames");
return 0;
```

- [ ] **Step 6: Ensure the project is in the solution + build**

If not already added in Task 2, add to `AlgoTradeForge.slnx`:
```xml
  <Project Path="src\AlgoTradeForge.DumpTicks\AlgoTradeForge.DumpTicks.csproj" />
```
Run: `dotnet build src/AlgoTradeForge.DumpTicks/`
Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add src/AlgoTradeForge.Live.Relay/RelayFrameFormatter.cs src/AlgoTradeForge.DumpTicks tests/AlgoTradeForge.Live.Relay.Tests/RelayFrameFormatterTests.cs AlgoTradeForge.slnx
git commit -m "feat(relay): frame formatter + dump-ticks CLI"
```

---

### Task 7: `TickRelayWriter` — bounded channel, drain, rotation, fsync, heartbeat, backpressure

**Files:**
- Create: `src/AlgoTradeForge.Live.Relay/ITickSegmentSink.cs`
- Create: `src/AlgoTradeForge.Live.Relay/LocalFileSegmentSink.cs`
- Create: `src/AlgoTradeForge.Live.Relay/TickRelayOptions.cs`
- Create: `src/AlgoTradeForge.Live.Relay/TickRelayWriter.cs`
- Test: `tests/AlgoTradeForge.Live.Relay.Tests/TickRelayWriterTests.cs`

**Interfaces:**
- Consumes: `Tick`, `TickSegmentWriter`, `TickSegmentHeader`, `SessionBoundaryReason`, `TimeProvider`.
- Produces:
  - `interface ITickSegmentSink { Stream BeginSegment(string instrument, long firstSequence, long createdAtMs); ValueTask CompleteSegment(string instrument, Stream segment, CancellationToken ct = default); }`
  - `sealed class LocalFileSegmentSink(string root) : ITickSegmentSink` — writes `{root}/{instrument}/{createdAtMs:D13}-{firstSequence:D19}.atft`.
  - `sealed record TickRelayOptions { int ChannelCapacity = 1<<16; long MaxSegmentBytes = 64L*1024*1024; TimeSpan HeartbeatInterval = 10s }`.
  - `sealed class TickRelayWriter : IAsyncDisposable` with `int RegisterInstrument(string instrument, sbyte priceScaleExp, sbyte qtyScaleExp)`, `bool TryEnqueue(int instrumentId, in Tick tick)`, `ValueTask Enqueue(int instrumentId, Tick tick, CancellationToken ct = default)`, `long DroppedCount { get; }`. The drain task opens a per-instrument segment on first tick (writing a `SessionStart` boundary), rotates at `MaxSegmentBytes` (Flush-to-disk + `CompleteSegment`), emits heartbeats to all open segments on the interval, and on disposal writes a `SessionEnd` boundary + flushes + completes every open segment.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/AlgoTradeForge.Live.Relay.Tests/TickRelayWriterTests.cs
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using Microsoft.Extensions.Time.Testing;

namespace AlgoTradeForge.Live.Relay.Tests;

public class TickRelayWriterTests
{
    private static List<RelayFrame> ReadAll(string dir, string instrument)
    {
        var frames = new List<RelayFrame>();
        var instrDir = Path.Combine(dir, instrument);
        foreach (var path in Directory.GetFiles(instrDir, "*.atft").OrderBy(p => p, StringComparer.Ordinal))
        {
            using var fs = File.OpenRead(path);
            using var reader = new TickSegmentReader(fs);
            while (reader.TryReadFrame(out var f)) frames.Add(f);
        }
        return frames;
    }

    [Fact]
    public async Task AllEnqueuedTicks_ArePersistedInOrder_AcrossRotation()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"relay_{Guid.NewGuid():N}");
        var sink = new LocalFileSegmentSink(dir);
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));

        // MaxSegmentBytes tiny so 200 ticks force several rotations (header 64 + 40/frame).
        var options = new TickRelayOptions { MaxSegmentBytes = 64 + 40 * 16 };

        await using (var writer = new TickRelayWriter(sink, options, time))
        {
            int id = writer.RegisterInstrument("ESZ5", priceScaleExp: 2, qtyScaleExp: 0);
            for (int i = 0; i < 200; i++)
                await writer.Enqueue(id, new Tick(1_700_000_000_000 + i, 5_000_000 + i, 1, i + 1, TickFlags.None));
        }

        var ticks = ReadAll(dir, "ESZ5").Where(f => f.Type == FrameType.Tick).ToList();
        Assert.Equal(200, ticks.Count);
        for (int i = 0; i < 200; i++)
            Assert.Equal(i + 1, ticks[i].Tick.Sequence);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task SlowSink_AppliesBackpressure_WithoutDroppingViaEnqueue()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"relay_{Guid.NewGuid():N}");
        var sink = new LocalFileSegmentSink(dir);
        var time = new FakeTimeProvider();
        var options = new TickRelayOptions { ChannelCapacity = 8 };

        await using (var writer = new TickRelayWriter(sink, options, time))
        {
            int id = writer.RegisterInstrument("NQZ5", 2, 0);
            for (int i = 0; i < 500; i++)
                await writer.Enqueue(id, new Tick(i, 100 + i, 1, i + 1, TickFlags.None));
            Assert.Equal(0, writer.DroppedCount);
        }

        var ticks = ReadAll(dir, "NQZ5").Count(f => f.Type == FrameType.Tick);
        Assert.Equal(500, ticks);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Heartbeat_IsWritten_WhenTimerAdvances()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"relay_{Guid.NewGuid():N}");
        var sink = new LocalFileSegmentSink(dir);
        var time = new FakeTimeProvider();
        var options = new TickRelayOptions { HeartbeatInterval = TimeSpan.FromSeconds(5) };

        await using (var writer = new TickRelayWriter(sink, options, time))
        {
            int id = writer.RegisterInstrument("CLZ5", 2, 0);
            await writer.Enqueue(id, new Tick(1, 100, 1, 1, TickFlags.None));
            await writer.WaitForDrain();              // ensure the tick (and SessionStart) are written
            time.Advance(TimeSpan.FromSeconds(6));     // fire one heartbeat tick
            await writer.WaitForDrain();
        }

        var frames = ReadAll(dir, "CLZ5");
        Assert.Contains(frames, f => f.Type == FrameType.Heartbeat);
        Assert.Contains(frames, f => f.Type == FrameType.SessionBoundary &&
                                     f.ReasonCode == (byte)SessionBoundaryReason.SessionStart);
        Assert.Contains(frames, f => f.Type == FrameType.SessionBoundary &&
                                     f.ReasonCode == (byte)SessionBoundaryReason.SessionEnd);

        Directory.Delete(dir, recursive: true);
    }
}
```

> Note: `FakeTimeProvider` is in `Microsoft.Extensions.TimeProvider.Testing`. Add the package to the test csproj: `<PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="9.10.0" />` (latest 9.x; verify with `dotnet add` if it fails to restore). `WaitForDrain()` is a test-support method on the writer that completes when the drain loop has processed everything enqueued so far.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.Live.Relay.Tests/ --filter FullyQualifiedName~TickRelayWriterTests`
Expected: FAIL — `TickRelayWriter` / `LocalFileSegmentSink` / options do not exist.

- [ ] **Step 3: Write the sink + options**

```csharp
// src/AlgoTradeForge.Live.Relay/ITickSegmentSink.cs
namespace AlgoTradeForge.Live.Relay;

public interface ITickSegmentSink
{
    Stream BeginSegment(string instrument, long firstSequence, long createdAtMs);
    ValueTask CompleteSegment(string instrument, Stream segment, CancellationToken ct = default);
}
```

```csharp
// src/AlgoTradeForge.Live.Relay/LocalFileSegmentSink.cs
using System.Globalization;

namespace AlgoTradeForge.Live.Relay;

public sealed class LocalFileSegmentSink(string root) : ITickSegmentSink
{
    public Stream BeginSegment(string instrument, long firstSequence, long createdAtMs)
    {
        var dir = Path.Combine(root, instrument);
        Directory.CreateDirectory(dir);
        var name = string.Create(CultureInfo.InvariantCulture,
            $"{createdAtMs:D13}-{firstSequence:D19}.atft");
        var path = Path.Combine(dir, name);
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 1 << 16);
    }

    public ValueTask CompleteSegment(string instrument, Stream segment, CancellationToken ct = default)
    {
        segment.Dispose();
        return ValueTask.CompletedTask;
    }
}
```

```csharp
// src/AlgoTradeForge.Live.Relay/TickRelayOptions.cs
namespace AlgoTradeForge.Live.Relay;

public sealed record TickRelayOptions
{
    public int ChannelCapacity { get; init; } = 1 << 16;
    public long MaxSegmentBytes { get; init; } = 64L * 1024 * 1024;
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);
}
```

- [ ] **Step 4: Write the relay writer**

```csharp
// src/AlgoTradeForge.Live.Relay/TickRelayWriter.cs
using System.Threading.Channels;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Live.Relay;

public sealed class TickRelayWriter : IAsyncDisposable
{
    private const int HeartbeatCommandId = -1;

    private readonly ITickSegmentSink _sink;
    private readonly TickRelayOptions _options;
    private readonly TimeProvider _time;
    private readonly Channel<Envelope> _channel;
    private readonly List<InstrumentState> _instruments = [];
    private readonly Task _drain;
    private readonly Task _heartbeat;
    private readonly CancellationTokenSource _cts = new();

    private long _dropped;
    private TaskCompletionSource _drainIdle = NewIdleSource();

    public TickRelayWriter(ITickSegmentSink sink, TickRelayOptions options, TimeProvider time)
    {
        _sink = sink;
        _options = options;
        _time = time;
        _channel = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(options.ChannelCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _drain = Task.Run(DrainLoop);
        _heartbeat = Task.Run(HeartbeatLoop);
    }

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public int RegisterInstrument(string instrument, sbyte priceScaleExp, sbyte qtyScaleExp)
    {
        lock (_instruments)
        {
            _instruments.Add(new InstrumentState
            {
                Instrument = instrument,
                PriceScaleExp = priceScaleExp,
                QtyScaleExp = qtyScaleExp,
            });
            return _instruments.Count - 1;
        }
    }

    public bool TryEnqueue(int instrumentId, in Tick tick)
    {
        if (_channel.Writer.TryWrite(new Envelope(instrumentId, tick))) return true;
        Interlocked.Increment(ref _dropped);
        return false;
    }

    public ValueTask Enqueue(int instrumentId, Tick tick, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(new Envelope(instrumentId, tick), ct);

    // Test support: completes once the drain has caught up to everything enqueued so far.
    public Task WaitForDrain()
    {
        var probe = Volatile.Read(ref _drainIdle);
        _channel.Writer.TryWrite(new Envelope(HeartbeatCommandId, default));
        return probe.Task;
    }

    private async Task DrainLoop()
    {
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var env))
                    await Handle(env).ConfigureAwait(false);

                var idle = Interlocked.Exchange(ref _drainIdle, NewIdleSource());
                idle.TrySetResult();
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var st in _instruments)
                await CloseSegment(st, SessionBoundaryReason.SessionEnd).ConfigureAwait(false);
            Volatile.Read(ref _drainIdle).TrySetResult();
        }
    }

    private async Task Handle(Envelope env)
    {
        if (env.InstrumentId == HeartbeatCommandId)
        {
            long now = _time.GetUtcNow().ToUnixTimeMilliseconds();
            foreach (var st in _instruments)
                st.Writer?.WriteHeartbeat(now);
            return;
        }

        var state = _instruments[env.InstrumentId];
        EnsureSegment(state, env.Tick.Sequence);
        if (state.BytesInSegment + RelayFormat.FrameSize > _options.MaxSegmentBytes)
        {
            await CloseSegment(state, null).ConfigureAwait(false);
            EnsureSegment(state, env.Tick.Sequence);
        }

        state.Writer!.WriteTick(env.Tick);
        state.BytesInSegment += RelayFormat.FrameSize;
    }

    private void EnsureSegment(InstrumentState st, long firstSequence)
    {
        if (st.Writer is not null) return;

        long now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        st.Stream = _sink.BeginSegment(st.Instrument, firstSequence, now);
        var header = new TickSegmentHeader(st.PriceScaleExp, st.QtyScaleExp, 0, now, firstSequence);
        st.Writer = new TickSegmentWriter(st.Stream, header, leaveOpen: true);
        st.BytesInSegment = RelayFormat.HeaderSize;
        st.Writer.WriteSessionBoundary(now, SessionBoundaryReason.SessionStart);
        st.BytesInSegment += RelayFormat.FrameSize;
    }

    private async Task CloseSegment(InstrumentState st, SessionBoundaryReason? finalMarker)
    {
        if (st.Writer is null || st.Stream is null) return;

        if (finalMarker is { } reason)
            st.Writer.WriteSessionBoundary(_time.GetUtcNow().ToUnixTimeMilliseconds(), reason);

        st.Writer.Flush(toDisk: true);
        await _sink.CompleteSegment(st.Instrument, st.Stream, _cts.Token).ConfigureAwait(false);
        st.Writer = null;
        st.Stream = null;
        st.BytesInSegment = 0;
    }

    private async Task HeartbeatLoop()
    {
        try
        {
            using var timer = new PeriodicTimer(_options.HeartbeatInterval, _time);
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
                _channel.Writer.TryWrite(new Envelope(HeartbeatCommandId, default));
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _drain.ConfigureAwait(false);
        _cts.Cancel();
        try { await _heartbeat.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _cts.Dispose();
    }

    private static TaskCompletionSource NewIdleSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly record struct Envelope(int InstrumentId, Tick Tick);

    private sealed class InstrumentState
    {
        public required string Instrument { get; init; }
        public sbyte PriceScaleExp { get; init; }
        public sbyte QtyScaleExp { get; init; }
        public TickSegmentWriter? Writer { get; set; }
        public Stream? Stream { get; set; }
        public long BytesInSegment { get; set; }
    }
}
```

> Drain-loop ordering note: `DisposeAsync` completes the channel *before* cancelling `_cts`, so the drain finishes every queued envelope and writes each instrument's `SessionEnd` boundary before the heartbeat loop is torn down. The `PeriodicTimer(TimeSpan, TimeProvider)` overload is what lets `FakeTimeProvider.Advance` drive heartbeats deterministically.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.Live.Relay.Tests/ --filter FullyQualifiedName~TickRelayWriterTests`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add src/AlgoTradeForge.Live.Relay/ITickSegmentSink.cs src/AlgoTradeForge.Live.Relay/LocalFileSegmentSink.cs src/AlgoTradeForge.Live.Relay/TickRelayOptions.cs src/AlgoTradeForge.Live.Relay/TickRelayWriter.cs tests/AlgoTradeForge.Live.Relay.Tests/TickRelayWriterTests.cs tests/AlgoTradeForge.Live.Relay.Tests/AlgoTradeForge.Live.Relay.Tests.csproj
git commit -m "feat(relay): bounded multi-instrument relay writer with rotation, fsync, heartbeat"
```

---

### Task 8: `SegmentUploader` — sweep completed local segments to `IFileStorage`

**Files:**
- Create: `src/AlgoTradeForge.Live.Relay/SegmentUploader.cs`
- Test: `tests/AlgoTradeForge.Live.Relay.Tests/SegmentUploaderTests.cs`

**Interfaces:**
- Consumes: `IFileStorage` (`AlgoTradeForge.Storage` namespace), the `.atft` files written by `LocalFileSegmentSink`.
- Produces: `sealed class SegmentUploader(IFileStorage storage, string localRoot, string keyPrefix)` with `Task<int> SweepOnce(CancellationToken ct = default)` — uploads every `*.atft` lacking a sibling `*.atft.uploaded` marker to `{keyPrefix}/{instrument}/{filename}`, writes the marker on success, returns the number uploaded. A failed upload leaves no marker (retried next sweep). Decouples archival push from capture: a stalled `IFileStorage` only delays the sweep; the relay writer keeps writing local segments.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/AlgoTradeForge.Live.Relay.Tests/SegmentUploaderTests.cs
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.Storage;
using NSubstitute;

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
        await File.WriteAllBytesAsync(segPath, payload);

        var captured = new Dictionary<string, byte[]>();
        var storage = Substitute.For<IFileStorage>();
        storage.WriteAllBytes(Arg.Any<string>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured[ci.ArgAt<string>(0)] = ci.ArgAt<ReadOnlyMemory<byte>>(1).ToArray();
                return Task.CompletedTask;
            });

        var uploader = new SegmentUploader(storage, root, keyPrefix: "live-md/ib/ticks");

        var first = await uploader.SweepOnce();
        var second = await uploader.SweepOnce();

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
            new byte[] { 9 });

        var storage = Substitute.For<IFileStorage>();
        storage.WriteAllBytes(Arg.Any<string>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new IOException("S3 down"));

        var uploader = new SegmentUploader(storage, root, "live-md/ib/ticks");

        await Assert.ThrowsAsync<IOException>(() => uploader.SweepOnce());
        Assert.Empty(Directory.GetFiles(instrDir, "*.uploaded"));

        Directory.Delete(root, recursive: true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.Live.Relay.Tests/ --filter FullyQualifiedName~SegmentUploaderTests`
Expected: FAIL — `SegmentUploader` does not exist.

- [ ] **Step 3: Write the uploader**

```csharp
// src/AlgoTradeForge.Live.Relay/SegmentUploader.cs
using AlgoTradeForge.Storage;

namespace AlgoTradeForge.Live.Relay;

public sealed class SegmentUploader(IFileStorage storage, string localRoot, string keyPrefix)
{
    public async Task<int> SweepOnce(CancellationToken ct = default)
    {
        if (!Directory.Exists(localRoot)) return 0;

        int uploaded = 0;
        foreach (var path in Directory.EnumerateFiles(localRoot, "*.atft", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var marker = path + ".uploaded";
            if (File.Exists(marker)) continue;

            var instrument = Path.GetFileName(Path.GetDirectoryName(path))!;
            var fileName = Path.GetFileName(path);
            var key = $"{keyPrefix}/{instrument}/{fileName}";

            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            await storage.WriteAllBytes(key, bytes, ct).ConfigureAwait(false);

            await File.WriteAllTextAsync(marker, key, ct).ConfigureAwait(false);
            uploaded++;
        }
        return uploaded;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.Live.Relay.Tests/ --filter FullyQualifiedName~SegmentUploaderTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.Live.Relay/SegmentUploader.cs tests/AlgoTradeForge.Live.Relay.Tests/SegmentUploaderTests.cs
git commit -m "feat(relay): segment uploader sweeps completed segments to IFileStorage"
```

---

### Task 9: Firehose benchmark — allocation + throughput

**Files:**
- Create: `benchmarks/AlgoTradeForge.Benchmarks/Benchmarks/TickRelayBenchmarks.cs`
- Modify: `benchmarks/AlgoTradeForge.Benchmarks/AlgoTradeForge.Benchmarks.csproj` (add relay project reference)

**Interfaces:**
- Consumes: `TickRelayWriter`, `LocalFileSegmentSink`, `TickRelayOptions`, `Tick` (no new production types).

- [ ] **Step 1: Add the project reference**

Add to `benchmarks/AlgoTradeForge.Benchmarks/AlgoTradeForge.Benchmarks.csproj` inside the existing `<ItemGroup>` of `<ProjectReference>`s:
```xml
    <ProjectReference Include="..\..\src\AlgoTradeForge.Live.Relay\AlgoTradeForge.Live.Relay.csproj" />
```

- [ ] **Step 2: Write the benchmark**

```csharp
// benchmarks/AlgoTradeForge.Benchmarks/Benchmarks/TickRelayBenchmarks.cs
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using BenchmarkDotNet.Attributes;

namespace AlgoTradeForge.Benchmarks.Benchmarks;

/// <summary>
/// GC-free ingest→archival throughput for the binary tick relay. Drives a synthetic
/// 1000-instrument firehose through <see cref="TickRelayWriter"/> to a temp-dir
/// <see cref="LocalFileSegmentSink"/>. <c>[MemoryDiagnoser]</c> + <c>[Config(typeof(BriefJsonConfig))]</c>
/// per repo convention; the headline number is Allocated, not just Mean.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(BriefJsonConfig))]
public class TickRelayBenchmarks
{
    private const int Instruments = 1000;
    private const int TicksPerInstrument = 100;

    private string _tempDir = null!;

    [GlobalSetup]
    public void Setup() => _tempDir = Path.Combine(Path.GetTempPath(), $"RelayBench_{Guid.NewGuid():N}");

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Benchmark]
    public async Task Relay_1000Instruments_100TicksEach()
    {
        var sink = new LocalFileSegmentSink(_tempDir);
        var options = new TickRelayOptions { MaxSegmentBytes = 8L * 1024 * 1024 };
        await using var writer = new TickRelayWriter(sink, options, TimeProvider.System);

        var ids = new int[Instruments];
        for (int i = 0; i < Instruments; i++)
            ids[i] = writer.RegisterInstrument($"SYM{i:D4}", priceScaleExp: 2, qtyScaleExp: 0);

        long seq = 0;
        for (int t = 0; t < TicksPerInstrument; t++)
            for (int i = 0; i < Instruments; i++)
                await writer.Enqueue(ids[i], new Tick(t, 5_000_000 + i, 1, ++seq, TickFlags.None));
    }
}
```

- [ ] **Step 3: Build the benchmark project**

Run: `dotnet build benchmarks/AlgoTradeForge.Benchmarks/ -c Release`
Expected: Build succeeded.

- [ ] **Step 4: Capture a baseline (dry job for a quick smoke, full job for real numbers)**

Run (smoke, fast): `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/perf/save-baseline.ps1 -Filter '*TickRelay*' -Job dry -Label 'tick-relay-initial'`
Expected: a `*-report-brief.json` archived under `~/.algo-tradeforge/perf-history/...`; the run completes with a reported Allocated figure. Record the Allocated/op as the baseline for future regressions (target: allocation per tick is dominated by the fixed segment/file buffers, not per-tick heap — the per-tick steady-state allocation should be ~0).

> Pre-flight: ensure no other `dotnet` process is running (the one-process rule + CPU contention destroys the signal).

- [ ] **Step 5: Commit**

```bash
git add benchmarks/AlgoTradeForge.Benchmarks/Benchmarks/TickRelayBenchmarks.cs benchmarks/AlgoTradeForge.Benchmarks/AlgoTradeForge.Benchmarks.csproj
git commit -m "bench(relay): 1000-instrument firehose allocation + throughput benchmark"
```

---

## Final verification

- [ ] **Build the whole solution:** `dotnet build AlgoTradeForge.slnx` → succeeds.
- [ ] **Run the relay test project:** `dotnet test tests/AlgoTradeForge.Live.Relay.Tests/` → all pass.
- [ ] **Run the domain test project:** `dotnet test tests/AlgoTradeForge.Domain.Tests/ --filter FullyQualifiedName~TickTests` → pass.
- [ ] **Smoke the CLI:** build `dump-ticks`, run it against a segment produced by `TickRelayWriterTests` output (or a throwaway `TickSegmentWriter` file) → prints header + frames + count.

## Self-Review (completed during planning)

- **Spec coverage:** binary tick framing (Tasks 3–5), heartbeat + session-boundary markers (Tasks 4, 7), losslessness via append + fsync-on-rotation + bounded channel with local-file spill (Task 7), separation of archival push from capture via the uploader sweep (Task 8), `dump-ticks` inspectability (Task 6), synthetic 1000-instrument firehose with allocation measurement (Task 9). The spec's §E "32 B record" is realized as the 40-byte frame here (the extra 8 bytes carry the frame-type/flags discriminator + reserved alignment that the prose's "ts/price/size/flags" omitted) — this is the deliberate, documented refinement; the vision §2 edit already says "fixed-width records" without pinning 32. Out of Plan-1 scope (correctly deferred to later plans): HistoryLoader canonicalization of binary segments, `ITickRouter`/`IStrategyDispatch`/`IOrderRouter`, strategy `OnTick`, `collection.json` roles.
- **Placeholder scan:** none — every code step contains complete, compilable code; the only conditional instruction (where to register the DumpTicks project) is resolved by adding it in Task 6.
- **Type consistency:** `Tick` fields (`TimestampMs, Price, Quantity, Sequence, Flags`) are identical across Tasks 1, 4, 5, 7, 9. `RelayFormat.FrameSize`/`HeaderSize` used uniformly. `ITickSegmentSink.BeginSegment(instrument, firstSequence, createdAtMs)` matches its `LocalFileSegmentSink` impl and `TickRelayWriter.EnsureSegment` call. `IFileStorage.WriteAllBytes(key, ReadOnlyMemory<byte>, ct)` matches the real interface signature confirmed in source.
