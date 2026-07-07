# Binance Archive Backfill — Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Materialize replenishable Binance feeds (candles/candle-ext, mark-price, OI + 3×LS-ratio) from data.binance.vision monthly/daily archives, and expose an on-demand load API (`/api/v1/loads`, `/api/v1/coverage`) with background jobs. Eager collection is UNCHANGED in this phase.

**Spec:** `docs/superpowers/specs/2026-07-07-binance-archive-backfill-design.md` (read it first).

**Architecture:** New `IArchiveMaterializer` family (Infrastructure) behind an exchange-keyed `ArchiveMaterializerRegistry` (Application) — the registry IS the replenishability classification. `SymbolCollector.CollectFeedAsync` gains an archive-first branch: missing/incomplete whole months are materialized by atomic partition replacement (temp file + `File.Move` overwrite — NOT via `BufferedPartitionWriter`, whose monotonic watermark would silently drop re-materialized rows), then the existing REST collector fills the tail. Load jobs mirror the Aggregation-jobs pattern in a simplified form.

**Ownership rule (load-bearing):** the archive path touches **fully closed months only** — months whose end precedes the start of the current UTC month. The current month is owned exclusively by the existing REST/stream tail. This single boundary (a) keeps the REST tail alive every eager run (archive returns `start-of-current-month`, never `toMs`), (b) dissolves the `BufferedPartitionWriter` watermark race — a closed month is never an active append target, so replacement cannot erase rows the in-process writer thinks it already wrote (its watermark only ratchets upward and would silently drop re-appends), and (c) prevents re-downloading up to 31 daily zips per feed per eager run, since closed complete months are skipped by the coverage predicate and the current month is never archive-touched.

**Tech Stack:** C# 14 / .NET 10, xunit.v3 + NSubstitute (existing test project `tests/AlgoTradeForge.HistoryLoader.Tests/`), `System.IO.Compression` (in-box), no new NuGet packages.

## Global Constraints

- **NO commits and NO `git add` by the executor.** Andrew reviews and commits himself. Each task ends with tests passing and changes left unstaged.
- **Only ONE `dotnet` process at a time.** Never run build/test in parallel.
- Test command: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter "FullyQualifiedName~<TestClass>"`.
- No `Async` suffix on new async methods; `CancellationToken ct = default` on every async signature.
- One type per file, file named after the type (single-line records accompanying an interface may share its file).
- Comments: only non-obvious facts, terse.
- `using var` over try/finally for pure releases.
- Candle CSV values are scaled longs via `MoneyConvert.ToLong(value * 10^decimalDigits)` — NEVER raw `(long)` casts.
- Timestamps/counters are plain `long` ms — raw casts fine there.
- New public Application types live in `AlgoTradeForge.HistoryLoader.Application.Archive` namespace; Infrastructure implementations in `AlgoTradeForge.HistoryLoader.Infrastructure.Archive`.
- **Tests:** pass `TestContext.Current.CancellationToken` to every awaited call in test bodies (xunit.v3 analyzer xUnit1051 — the snippets below omit it for brevity; add it when transcribing).
- **`SemaphoreSlim` acquisition:** `using var _ = await _gate.LockAsync(ct);` via `SemaphoreSlimExtensions` (`AlgoTradeForge.Storage.Threading`) — never `WaitAsync` + `try/finally` (Constitution v1.9.1).
- **`DataGap` semantics (both ends are PRESENT rows):** `FromMs` = last present row before the hole, `ToMs` = first present row after it; missing rows strictly inside = `(ToMs − FromMs)/interval − 1`. Task 6 and Task 7 both depend on this exact convention.

## Existing interfaces you will consume (verified signatures)

```csharp
// Application.Abstractions
public interface IFeedWriter {
    void Write(string assetDir, string feedName, string interval, string[] columns, FeedRecord record);
    Task<long?> ResumeFrom(string assetDir, string feedName, string interval, CancellationToken ct = default);
}
public interface ICandleWriter {
    void Write(string assetDir, string interval, CandleRecord record, int decimalDigits);
    Task<long?> ResumeFrom(string assetDir, string interval, CancellationToken ct = default);
}
public interface ISchemaManager {
    Task EnsureSchema(string assetDir, string feedName, string interval, string[] columns, AutoApplySpec? autoApply = null, CancellationToken ct = default);
    Task EnsureCandleConfig(string assetDir, int decimalDigits, string interval, CancellationToken ct = default);
    // ...more members not needed here
}
public interface IFeedStatusStore {
    Task<FeedStatus?> Load(string assetDir, string feedName, string interval, CancellationToken ct = default);
    Task Save(string assetDir, string feedName, string interval, FeedStatus status, CancellationToken ct = default);
}
// Domain
public readonly record struct FeedRecord(long TimestampMs, double[] Values);
public readonly record struct CandleRecord(long TimestampMs, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume) { public double[]? ExtValues { get; init; } }
public readonly record struct DataGap { public long FromMs { get; init; } public long ToMs { get; init; } }
// FeedStatus: FeedName, Interval, FirstTimestamp, LastTimestamp, LastRunUtc, RecordCount, Gaps (IReadOnlyList<DataGap>), Health
// Collection
public interface IFeedCollector {
    string FeedName { get; }
    bool SupportsSpot { get; }
    Task CollectAsync(AssetCollectionConfig assetConfig, FeedCollectionConfig feedConfig, string assetDir, long fromMs, long toMs, CancellationToken ct);
}
// SymbolCollector.CollectFeedAsync(AssetCollectionConfig, FeedCollectionConfig, string assetDir, long fromMs, long toMs, CancellationToken)
// BackfillOrchestrator.TryRunSingleAsync(AssetCollectionConfig asset, string assetDir, IReadOnlyList<string>? feedFilter = null, DateOnly? fromDate = null, CancellationToken ct = default) : Task<bool>
// BackfillOrchestrator.ResolveAssetDir(string dataRoot, AssetCollectionConfig asset) : string
// AssetPathConvention.DirectoryName(string symbol, string assetType) : string  ("BTCUSDT_perp" for perpetual/future)
// AssetTypes.IsSpot(string) / AssetTypes.IsFutures(string); constants AssetTypes.Spot/Perpetual/Future/Equity
// FeedNames: Candles, CandleExt, MarkPrice, OpenInterest, LsRatioGlobal, LsRatioTopAccounts, LsRatioTopPositions, Ticks, Liquidations...
// IntervalParser.ToTimeSpan(string interval) : TimeSpan
```

**Partition file formats (must be replicated byte-compatibly by materializers):**

- Candles: `{assetDir}/candles/{yyyy-MM}_{interval}.csv`, header `ts,o,h,l,c,vol`, row `{tsMs},{o},{h},{l},{c},{vol}` where o..vol = `MoneyConvert.ToLong(decimal * 10^decimalDigits)`.
- Generic feeds: `{assetDir}/{feedName}/{yyyy-MM}_{interval}.csv` (no `_{interval}` suffix when interval is empty), header `ts,{col1},{col2},...`, row `{tsMs},{v1},{v2},...`, doubles in `InvariantCulture`.

**Archive URL shapes (verified against live S3 listings):**

- Monthly klines: `https://data.binance.vision/data/{market}/monthly/{dataset}/{SYMBOL}/{interval}/{SYMBOL}-{interval}-{yyyy-MM}.zip` where market = `spot` | `futures/um`, dataset = `klines` | `markPriceKlines`.
- Daily klines: same with `daily` and `-{yyyy-MM-dd}.zip`.
- Daily metrics (futures-only, daily-only): `https://data.binance.vision/data/futures/um/daily/metrics/{SYMBOL}/{SYMBOL}-metrics-{yyyy-MM-dd}.zip`.
- Checksum: `<zip url>.CHECKSUM`, content `"{sha256hex}  {filename}"`.
- Kline CSV columns (12): `open_time, open, high, low, close, volume, close_time, quote_volume, count, taker_buy_volume, taker_buy_quote_volume, ignore`. Older files have NO header; newer may. Spot timestamps are **microseconds from 2025-01**, milliseconds before.
- Metrics CSV columns (8, WITH header, 5-minute rows, `create_time` is a **datetime string** `"yyyy-MM-dd HH:mm:ss"` UTC, not epoch): `create_time, symbol, sum_open_interest, sum_open_interest_value, count_toptrader_long_short_ratio, sum_toptrader_long_short_ratio, count_long_short_ratio, sum_taker_long_short_vol_ratio`.
- `candle-ext` columns (futures): `["quote_vol","trade_count","taker_buy_vol","taker_buy_quote_vol","taker_buy_trade_count"]` — the last is a synthesized volume-weighted proxy `trade_count * taker_buy_vol / vol` clamped to `[0, trade_count]` (same formula as `BinanceKlineParser`, see `src/AlgoTradeForge.HistoryLoader.Infrastructure/Binance/BinanceKlineParser.cs:41-55`).

---

### Task 1: ArchiveCsv parsing helpers

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Archive/ArchiveCsv.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Archive/ArchiveCsvTests.cs`

**Interfaces:**
- Produces: `static IEnumerable<string[]> ArchiveCsv.ReadRows(TextReader reader)` (skips header row when first cell is non-numeric), `static long ArchiveCsv.NormalizeTimestampMs(long raw)` (µs→ms when `raw >= 100_000_000_000_000`).

- [ ] **Step 1: Write the failing tests**

```csharp
using AlgoTradeForge.HistoryLoader.Application.Archive;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class ArchiveCsvTests
{
    [Fact]
    public void ReadRows_SkipsHeaderRow_WhenPresent()
    {
        using var reader = new StringReader("open_time,open,high\n1000,1.5,2.5\n2000,2.5,3.5\n");
        var rows = ArchiveCsv.ReadRows(reader).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("1000", rows[0][0]);
    }

    [Fact]
    public void ReadRows_KeepsFirstRow_WhenNoHeader()
    {
        using var reader = new StringReader("1000,1.5,2.5\n2000,2.5,3.5\n");
        var rows = ArchiveCsv.ReadRows(reader).ToList();
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void ReadRows_SkipsEmptyLines()
    {
        using var reader = new StringReader("1000,1.5\n\n2000,2.5\n");
        Assert.Equal(2, ArchiveCsv.ReadRows(reader).Count());
    }

    [Fact]
    public void NormalizeTimestampMs_PassesThroughMilliseconds()
    {
        Assert.Equal(1_751_846_400_000, ArchiveCsv.NormalizeTimestampMs(1_751_846_400_000));
    }

    [Fact]
    public void NormalizeTimestampMs_ConvertsMicroseconds()
    {
        // Spot archive switched to microseconds on 2025-01-01.
        Assert.Equal(1_751_846_400_000, ArchiveCsv.NormalizeTimestampMs(1_751_846_400_000_000));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter "FullyQualifiedName~ArchiveCsvTests"`
Expected: compile error `ArchiveCsv does not exist` — create the skeleton below with `throw new NotImplementedException()` bodies first if you want a clean assertion-level RED, then re-run: FAIL.

- [ ] **Step 3: Write the implementation**

```csharp
namespace AlgoTradeForge.HistoryLoader.Application.Archive;

public static class ArchiveCsv
{
    public static IEnumerable<string[]> ReadRows(TextReader reader)
    {
        string? line;
        var first = true;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
                continue;
            if (first)
            {
                first = false;
                if (!char.IsAsciiDigit(line[0]))
                    continue;
            }
            yield return line.Split(',');
        }
    }

    // ms epochs stay < 1e14 until year 5138; µs epochs are >= 1e14 for any date after 1973.
    public static long NormalizeTimestampMs(long raw) =>
        raw >= 100_000_000_000_000 ? raw / 1000 : raw;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter "FullyQualifiedName~ArchiveCsvTests"`
Expected: PASS (5/5). Leave changes unstaged.

---

### Task 2: IArchiveMaterializer + ArchiveMaterializerRegistry (classification)

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Archive/IArchiveMaterializer.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Archive/ArchiveMaterializerRegistry.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Archive/ArchiveMaterializerRegistryTests.cs`

**Interfaces:**
- Produces:

```csharp
public interface IArchiveMaterializer
{
    string Exchange { get; }   // lowercase, e.g. "binance"
    string FeedName { get; }   // FeedNames.* constant it produces
    bool Supports(string assetType);
    Task<ArchiveMonthResult> MaterializeMonth(
        AssetCollectionConfig assetConfig,
        FeedCollectionConfig feedConfig,
        string assetDir,
        int year, int month,
        CancellationToken ct = default);
}
public readonly record struct ArchiveMonthResult(long RowsWritten, bool AvailableAtSource);

public sealed class ArchiveMaterializerRegistry
{
    public ArchiveMaterializerRegistry(IEnumerable<IArchiveMaterializer> materializers);
    public IArchiveMaterializer? Resolve(string exchange, string feedName, string assetType);
    public bool IsReplenishable(string exchange, string feedName, string assetType); // => Resolve(...) is not null
}
```

- [ ] **Step 1: Write the failing tests** (stub materializer via NSubstitute)

```csharp
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Domain;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class ArchiveMaterializerRegistryTests
{
    private static IArchiveMaterializer Stub(string exchange, string feed, bool futuresOnly = false)
    {
        var m = Substitute.For<IArchiveMaterializer>();
        m.Exchange.Returns(exchange);
        m.FeedName.Returns(feed);
        m.Supports(Arg.Any<string>()).Returns(ci => !futuresOnly || AssetTypes.IsFutures(ci.Arg<string>()));
        return m;
    }

    [Fact]
    public void Resolve_ReturnsMaterializer_ForRegisteredTuple()
    {
        var registry = new ArchiveMaterializerRegistry([Stub("binance", FeedNames.Candles)]);
        Assert.NotNull(registry.Resolve("binance", FeedNames.Candles, AssetTypes.Spot));
        Assert.True(registry.IsReplenishable("binance", FeedNames.Candles, AssetTypes.Perpetual));
    }

    [Fact]
    public void UnknownExchange_IsIrreplaceableByConstruction()
    {
        var registry = new ArchiveMaterializerRegistry([Stub("binance", FeedNames.Candles)]);
        Assert.False(registry.IsReplenishable("ib", FeedNames.Candles, AssetTypes.Equity));
    }

    [Fact]
    public void AssetTypeSensitivity_FuturesOnlyMaterializer_RejectsSpot()
    {
        var registry = new ArchiveMaterializerRegistry(
            [Stub("binance", FeedNames.OpenInterest, futuresOnly: true)]);
        Assert.True(registry.IsReplenishable("binance", FeedNames.OpenInterest, AssetTypes.Perpetual));
        Assert.False(registry.IsReplenishable("binance", FeedNames.OpenInterest, AssetTypes.Spot));
    }

    [Fact]
    public void UnregisteredFeed_IsIrreplaceable()
    {
        var registry = new ArchiveMaterializerRegistry([Stub("binance", FeedNames.Candles)]);
        Assert.False(registry.IsReplenishable("binance", FeedNames.Liquidations, AssetTypes.Perpetual));
    }

    [Fact]
    public void Resolve_IsCaseInsensitiveOnExchange()
    {
        var registry = new ArchiveMaterializerRegistry([Stub("binance", FeedNames.Candles)]);
        Assert.NotNull(registry.Resolve("Binance", FeedNames.Candles, AssetTypes.Spot));
    }
}
```

- [ ] **Step 2: Run to verify FAIL** (types missing)

- [ ] **Step 3: Implement**

`IArchiveMaterializer.cs` (interface + `ArchiveMonthResult` record struct share the file per file-organization convention):

```csharp
using AlgoTradeForge.HistoryLoader.Application;

namespace AlgoTradeForge.HistoryLoader.Application.Archive;

/// <summary>
/// Materializes one monthly partition of one feed from a public archive.
/// Registration in <see cref="ArchiveMaterializerRegistry"/> is what makes a
/// (exchange, feed, assetType) tuple replenishable — venues without archive
/// sources (IB) are irreplaceable by construction.
/// </summary>
public interface IArchiveMaterializer
{
    string Exchange { get; }
    string FeedName { get; }
    bool Supports(string assetType);
    Task<ArchiveMonthResult> MaterializeMonth(
        AssetCollectionConfig assetConfig,
        FeedCollectionConfig feedConfig,
        string assetDir,
        int year, int month,
        CancellationToken ct = default);
}

public readonly record struct ArchiveMonthResult(long RowsWritten, bool AvailableAtSource);
```

`ArchiveMaterializerRegistry.cs`:

```csharp
namespace AlgoTradeForge.HistoryLoader.Application.Archive;

public sealed class ArchiveMaterializerRegistry
{
    private readonly ILookup<(string Exchange, string Feed), IArchiveMaterializer> _byKey;

    public ArchiveMaterializerRegistry(IEnumerable<IArchiveMaterializer> materializers) =>
        _byKey = materializers.ToLookup(m => (m.Exchange.ToLowerInvariant(), m.FeedName));

    public IArchiveMaterializer? Resolve(string exchange, string feedName, string assetType) =>
        _byKey[(exchange.ToLowerInvariant(), feedName)].FirstOrDefault(m => m.Supports(assetType));

    public bool IsReplenishable(string exchange, string feedName, string assetType) =>
        Resolve(exchange, feedName, assetType) is not null;
}
```

- [ ] **Step 4: Run tests → PASS.** Leave unstaged.

---

### Task 3: BinanceArchiveClient (download + checksum + unzip)

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Archive/IBinanceArchiveClient.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Archive/ArchiveIntegrityException.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/BinanceArchiveClient.cs`
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/HistoryLoaderOptions.cs` (extend `BinanceOptions`)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Archive/BinanceArchiveClientTests.cs`

**Interfaces:**
- Produces:

```csharp
public interface IBinanceArchiveClient
{
    // Returns the extracted CSV stream (temp-file backed, auto-deleted on dispose), or null on 404.
    // Throws ArchiveIntegrityException after one failed re-download on checksum mismatch.
    Task<Stream?> DownloadMonthly(string market, string dataset, string symbol, string? interval, int year, int month, CancellationToken ct = default);
    Task<Stream?> DownloadDaily(string market, string dataset, string symbol, string? interval, DateOnly date, CancellationToken ct = default);
}
```
- `market` is `"spot"` or `"futures/um"`. File-name token = `interval ?? dataset` (klines use interval, metrics/fundingRate use dataset name).

- [ ] **Step 1: Extend `BinanceOptions`** (config, no test needed — covered by client tests):

```csharp
// add to BinanceOptions in HistoryLoaderOptions.cs
public string ArchiveBaseUrl { get; init; } = "https://data.binance.vision";
public int ArchiveDownloadConcurrency { get; init; } = 4;
```

- [ ] **Step 2: Write the failing tests** (self-contained stub handler; in-memory zip via `System.IO.Compression`):

```csharp
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Infrastructure.Archive;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class BinanceArchiveClientTests
{
    private const string Csv = "1000,1.5,2.5,0.5,2.0,10\n";

    private static byte[] Zip(string entryName, string content)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry(entryName).Open();
            entry.Write(Encoding.UTF8.GetBytes(content));
        }
        return ms.ToArray();
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(responder(request));
        }
    }

    private static BinanceArchiveClient CreateClient(StubHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("binance-archive").Returns(_ =>
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://data.binance.vision") });
        var options = Options.Create(new HistoryLoaderOptions());
        return new BinanceArchiveClient(factory, options, NullLogger<BinanceArchiveClient>.Instance);
    }

    [Fact]
    public async Task DownloadMonthly_HappyPath_ReturnsExtractedCsv()
    {
        var zip = Zip("BTCUSDT-1h-2024-03.csv", Csv);
        var handler = new StubHandler(req =>
            req.RequestUri!.AbsolutePath.EndsWith(".CHECKSUM")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent($"{Sha256Hex(zip)}  BTCUSDT-1h-2024-03.zip") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });

        var client = CreateClient(handler);
        await using var stream = await client.DownloadMonthly("futures/um", "klines", "BTCUSDT", "1h", 2024, 3);

        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        Assert.Equal(Csv, await reader.ReadToEndAsync());
        Assert.Contains("/data/futures/um/monthly/klines/BTCUSDT/1h/BTCUSDT-1h-2024-03.zip", handler.Requests);
    }

    [Fact]
    public async Task Download_Returns_Null_On404()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient(handler);
        Assert.Null(await client.DownloadMonthly("spot", "klines", "BTCUSDT", "1m", 2019, 1));
    }

    [Fact]
    public async Task Download_Throws_OnPersistentChecksumMismatch()
    {
        var zip = Zip("BTCUSDT-metrics-2024-03-01.csv", Csv);
        var handler = new StubHandler(req =>
            req.RequestUri!.AbsolutePath.EndsWith(".CHECKSUM")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("deadbeef  BTCUSDT-metrics-2024-03-01.zip") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });

        var client = CreateClient(handler);
        await Assert.ThrowsAsync<ArchiveIntegrityException>(() =>
            client.DownloadDaily("futures/um", "metrics", "BTCUSDT", null, new DateOnly(2024, 3, 1)));
        // one original attempt + one retry = 2 zip downloads
        Assert.Equal(2, handler.Requests.Count(r => r.EndsWith(".zip")));
    }

    [Fact]
    public async Task DownloadDaily_BuildsMetricsUrl_WithoutIntervalSegment()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient(handler);
        await client.DownloadDaily("futures/um", "metrics", "BTCUSDT", null, new DateOnly(2024, 3, 1));
        Assert.Contains("/data/futures/um/daily/metrics/BTCUSDT/BTCUSDT-metrics-2024-03-01.zip", handler.Requests);
    }
}
```

Add `using NSubstitute;` to the usings.

- [ ] **Step 3: Run to verify FAIL** (types missing).

- [ ] **Step 4: Implement**

`ArchiveIntegrityException.cs` (Application/Archive):

```csharp
namespace AlgoTradeForge.HistoryLoader.Application.Archive;

public sealed class ArchiveIntegrityException(string url)
    : Exception($"Archive checksum mismatch after retry: {url}");
```

`IBinanceArchiveClient.cs` (Application/Archive): interface exactly as in the Interfaces block above.

`BinanceArchiveClient.cs` (Infrastructure/Archive):

```csharp
using System.IO.Compression;
using System.Security.Cryptography;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.Storage.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Archive;

internal sealed class BinanceArchiveClient : IBinanceArchiveClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BinanceArchiveClient> _logger;
    private readonly SemaphoreSlim _gate;

    public BinanceArchiveClient(
        IHttpClientFactory httpClientFactory,
        IOptions<HistoryLoaderOptions> options,
        ILogger<BinanceArchiveClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _gate = new SemaphoreSlim(options.Value.Binance.ArchiveDownloadConcurrency);
    }

    public Task<Stream?> DownloadMonthly(string market, string dataset, string symbol, string? interval, int year, int month, CancellationToken ct = default) =>
        Download(market, "monthly", dataset, symbol, interval, $"{year:D4}-{month:D2}", ct);

    public Task<Stream?> DownloadDaily(string market, string dataset, string symbol, string? interval, DateOnly date, CancellationToken ct = default) =>
        Download(market, "daily", dataset, symbol, interval, date.ToString("yyyy-MM-dd"), ct);

    private async Task<Stream?> Download(string market, string period, string dataset, string symbol, string? interval, string stamp, CancellationToken ct)
    {
        var token = interval ?? dataset;
        var dir = interval is null
            ? $"data/{market}/{period}/{dataset}/{symbol}"
            : $"data/{market}/{period}/{dataset}/{symbol}/{interval}";
        var url = $"{dir}/{symbol}-{token}-{stamp}.zip";

        using var _ = await _gate.LockAsync(ct);
        var client = _httpClientFactory.CreateClient("binance-archive");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var tempZip = await DownloadToTemp(client, url, ct);
            if (tempZip is null)
                return null;

            if (await VerifyChecksum(client, url, tempZip, ct))
                return ExtractSingleEntry(tempZip);

            File.Delete(tempZip);
            _logger.LogWarning("Checksum mismatch for {Url} (attempt {Attempt}/2)", url, attempt + 1);
        }
        throw new ArchiveIntegrityException(url);
    }

    // Transient failures (5xx / network) retry 3× with 1s/2s/4s backoff — spec §5 requires
    // retry-with-backoff and the repo bans new NuGet packages, so no resilience handler.
    private static async Task<string?> DownloadToTemp(HttpClient client, string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return null;
                response.EnsureSuccessStatusCode();

                var tempPath = Path.Combine(Path.GetTempPath(), $"atf-archive-{Guid.NewGuid():N}.zip");
                await using var file = File.Create(tempPath);
                await response.Content.CopyToAsync(file, ct);
                return tempPath;
            }
            catch (HttpRequestException) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct);
            }
        }
    }

    private static async Task<bool> VerifyChecksum(HttpClient client, string url, string tempZip, CancellationToken ct)
    {
        using var response = await client.GetAsync(url + ".CHECKSUM", ct);
        if (!response.IsSuccessStatusCode)
            return true; // no checksum published — accept the payload
        var text = await response.Content.ReadAsStringAsync(ct);
        var expected = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim();

        await using var file = File.OpenRead(tempZip);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    // Extracts the single CSV entry to a DeleteOnClose temp file so the zip can be removed
    // immediately and the returned stream self-cleans on dispose.
    private static Stream ExtractSingleEntry(string tempZip)
    {
        try
        {
            using var zip = ZipFile.OpenRead(tempZip);
            var entry = zip.Entries[0];
            var csvPath = Path.Combine(Path.GetTempPath(), $"atf-archive-{Guid.NewGuid():N}.csv");
            var output = new FileStream(
                csvPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 81920, FileOptions.DeleteOnClose);
            using (var entryStream = entry.Open())
                entryStream.CopyTo(output);
            output.Position = 0;
            return output;
        }
        finally
        {
            File.Delete(tempZip);
        }
    }
}
```

- [ ] **Step 5: Run tests → PASS.** Also run the full suite once (`dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/`) to catch compile breaks. Leave unstaged.

---

### Task 4: PartitionFileWriter (atomic month replacement)

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/PartitionFileWriter.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Archive/IPartitionFileWriter.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Archive/PartitionFileWriterTests.cs`

**Interfaces:**
- Produces:

```csharp
public interface IPartitionFileWriter
{
    // Writes header + rows to "<partitionPath>.tmp-<guid>" then atomically moves over partitionPath.
    Task ReplacePartition(string partitionPath, string header, IEnumerable<string> rows, CancellationToken ct = default);
}
```

**Why not the existing writers:** `BufferedPartitionWriter` (base of `CandleCsvWriter`/`FeedCsvWriter`) enforces a monotonic per-partition watermark — appending a re-materialized month whose rows precede the watermark would be silently dropped. Atomic whole-file replacement sidesteps that. The REST append path never conflicts because of the ownership rule (plan header): this writer only ever touches fully closed months, which are never an active append target — the current month's partition, the only one with a live in-process buffer/watermark, is never replaced.

- [ ] **Step 1: Failing test**

```csharp
using AlgoTradeForge.HistoryLoader.Infrastructure.Archive;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class PartitionFileWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"atf-pfw-{Guid.NewGuid():N}");
    public PartitionFileWriterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task ReplacePartition_CreatesFileWithHeaderAndRows()
    {
        var path = Path.Combine(_dir, "candles", "2024-03_1h.csv");
        var writer = new PartitionFileWriter();

        await writer.ReplacePartition(path, "ts,o,h,l,c,vol", ["1000,1,2,0,1,10", "2000,1,2,0,1,20"]);

        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(["ts,o,h,l,c,vol", "1000,1,2,0,1,10", "2000,1,2,0,1,20"], lines);
    }

    [Fact]
    public async Task ReplacePartition_OverwritesExistingPartialFile()
    {
        var path = Path.Combine(_dir, "2024-03.csv");
        await File.WriteAllTextAsync(path, "ts,x\n999,0.5\n");
        var writer = new PartitionFileWriter();

        await writer.ReplacePartition(path, "ts,x", ["1000,1.5"]);

        Assert.Equal(["ts,x", "1000,1.5"], await File.ReadAllLinesAsync(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*"));
    }
}
```

- [ ] **Step 2: Run → FAIL.**

- [ ] **Step 3: Implement**

```csharp
using AlgoTradeForge.HistoryLoader.Application.Archive;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Archive;

internal sealed class PartitionFileWriter : IPartitionFileWriter
{
    public async Task ReplacePartition(string partitionPath, string header, IEnumerable<string> rows, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(partitionPath)!);
        var tempPath = $"{partitionPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = File.Create(tempPath))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteLineAsync(header.AsMemory(), ct);
                foreach (var row in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    await writer.WriteLineAsync(row.AsMemory(), ct);
                }
            }
            try
            {
                File.Move(tempPath, partitionPath, overwrite: true);
            }
            catch (IOException)
            {
                // Windows: a concurrent reader (backtest loader) holding the partition open
                // fails the move; one short retry is cheap insurance before failing the job.
                await Task.Delay(500, ct);
                File.Move(tempPath, partitionPath, overwrite: true);
            }
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }
}
```

Note: `PartitionFileWriter` must be `internal sealed` + registered in DI later; the test project sees internals only if `InternalsVisibleTo` exists — check `src/AlgoTradeForge.HistoryLoader.Infrastructure/*.csproj` for an existing `InternalsVisibleTo AlgoTradeForge.HistoryLoader.Tests`; `FeedCsvWriter` is internal and has tests, so it exists. If not found, make the class `public sealed`.

- [ ] **Step 4: Run → PASS.** Leave unstaged.

---

### Task 5: KlinesArchiveMaterializer (candles + candle-ext + mark-price)

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/KlinesArchiveMaterializer.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Archive/KlinesArchiveMaterializerTests.cs`

**Interfaces:**
- Consumes: `IBinanceArchiveClient` (Task 3), `IPartitionFileWriter` (Task 4), `ISchemaManager`, `IFeedStatusStore`, `ArchiveCsv` (Task 1).
- Produces: `IArchiveMaterializer` implementation. Constructor:

```csharp
public KlinesArchiveMaterializer(
    string feedName,              // FeedNames.Candles or FeedNames.MarkPrice
    string dataset,               // "klines" or "markPriceKlines"
    bool supportsSpot,            // candles: true; mark-price: false
    IBinanceArchiveClient archive,
    IPartitionFileWriter partitionWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<KlinesArchiveMaterializer> logger)
```

**Behavior contract:**
1. `Exchange => "binance"`; `Supports(assetType)` = `supportsSpot || AssetTypes.IsFutures(assetType)`.
2. `MaterializeMonth`: try monthly zip; on null, assemble from daily zips for each day of the month up to `min(month end, today UTC)` — a missing day mid-month is skipped (recorded as no rows; the source gap surfaces through row count); if monthly AND all dailies are null → `new ArchiveMonthResult(0, AvailableAtSource: false)`.
3. Parse rows via `ArchiveCsv.ReadRows` + `ArchiveCsv.NormalizeTimestampMs(long.Parse(row[0]))`; keep only rows whose ts falls inside the month (archives occasionally carry a boundary row).
4. For `FeedNames.Candles`: build candle rows `"{ts},{o},{h},{l},{c},{vol}"` with `MoneyConvert.ToLong(decimal.Parse(row[i], CultureInfo.InvariantCulture) * multiplier)` where `multiplier = 10^assetConfig.DecimalDigits`; header `ts,o,h,l,c,vol`; partition path `{assetDir}/candles/{yyyy-MM}_{interval}.csv`. Call `SchemaManager.EnsureCandleConfig(assetDir, assetConfig.DecimalDigits, interval, ct)` once per call.
   For futures also write `candle-ext`: columns `["quote_vol","trade_count","taker_buy_vol","taker_buy_quote_vol","taker_buy_trade_count"]`, values from row indices 7,8,9,10 plus the proxy `trade_count * taker_buy_vol / vol` clamped to `[0, trade_count]` (0 when vol == 0); doubles rendered with `CultureInfo.InvariantCulture`; header `ts,quote_vol,...`; partition `{assetDir}/candle-ext/{yyyy-MM}_{interval}.csv`; `SchemaManager.EnsureSchema(assetDir, FeedNames.CandleExt, interval, ExtColumns, ct: ct)`.
5. For `FeedNames.MarkPrice`: columns `["o","h","l","c"]` as doubles from row indices 1–4, header `ts,o,h,l,c`, partition `{assetDir}/mark-price/{yyyy-MM}_{interval}.csv`, `EnsureSchema` accordingly.
6. After writing, merge `FeedStatus` (per feed touched): `FirstTimestamp = min(existing, monthFirst)`, `LastTimestamp = max(existing, monthLast)`, `RecordCount += rows`, keep existing `Gaps`, `Health` unchanged logic (`Healthy` if no gaps).
7. Return `new ArchiveMonthResult(rows, true)`.

- [ ] **Step 1: Failing tests** — key cases (use NSubstitute for `IBinanceArchiveClient` returning `MemoryStream` CSV fixtures, real `PartitionFileWriter` into a temp dir, NSubstitute for `ISchemaManager`/`IFeedStatusStore`):

```csharp
// fixture rows: 12-column kline CSV, no header (older format)
private const string KlineCsv =
    "1709251200000,50000.1,50100.2,49900.3,50050.4,12.5,1709254799999,625631.2,1500,6.25,312815.6,0\n" +
    "1709254800000,50050.4,50200.0,50000.0,50150.0,10.0,1709258399999,501500.0,1200,5.0,250750.0,0\n";
```

Tests to write (full bodies — follow the pattern of `PartitionFileWriterTests` temp-dir fixture):
1. `MaterializeMonth_Candles_WritesScaledPartition` — DecimalDigits=2 → first row becomes `1709251200000,5000010,5010020,4990030,5005040,1250`; file at `candles/2024-03_1h.csv`; header `ts,o,h,l,c,vol`.
2. `MaterializeMonth_Futures_WritesCandleExtWithProxy` — candle-ext row = `1709251200000,625631.2,1500,6.25,312815.6,750` (proxy = 1500 * 6.25 / 12.5 = 750); file at `candle-ext/2024-03_1h.csv`.
3. `MaterializeMonth_Spot_SkipsCandleExt` — assetType spot → no `candle-ext` directory.
4. `MaterializeMonth_MicrosecondTimestamps_Normalized` — same CSV with ts ×1000 (spot 2025+ format) → identical output ts.
5. `MaterializeMonth_MonthlyMissing_AssemblesFromDailies` — monthly returns null, dailies return per-day CSVs → rows concatenated; verify `DownloadDaily` called for each day of a past month.
6. `MaterializeMonth_NothingAtSource_ReportsUnavailable` — all downloads null → `AvailableAtSource == false`, no files written.
7. `MaterializeMonth_MarkPrice_WritesOhlcDoubles` — feedName mark-price, dataset markPriceKlines → `mark-price/2024-03_1h.csv` header `ts,o,h,l,c`, row `1709251200000,50000.1,50100.2,49900.3,50050.4`.
8. `MaterializeMonth_UpdatesFeedStatus` — `IFeedStatusStore.Save` called with `RecordCount` incremented by written rows and `LastTimestamp` = last row ts.

- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement** the class per the behavior contract. Skeleton:

```csharp
using System.Globalization;
using AlgoTradeForge.Domain;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Archive;

internal sealed class KlinesArchiveMaterializer(
    string feedName,
    string dataset,
    bool supportsSpot,
    IBinanceArchiveClient archive,
    IPartitionFileWriter partitionWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<KlinesArchiveMaterializer> logger) : IArchiveMaterializer
{
    private static readonly string[] ExtColumns =
        ["quote_vol", "trade_count", "taker_buy_vol", "taker_buy_quote_vol", "taker_buy_trade_count"];

    public string Exchange => "binance";
    public string FeedName => feedName;
    public bool Supports(string assetType) => supportsSpot || AssetTypes.IsFutures(assetType);

    public async Task<ArchiveMonthResult> MaterializeMonth(
        AssetCollectionConfig assetConfig, FeedCollectionConfig feedConfig,
        string assetDir, int year, int month, CancellationToken ct = default)
    {
        var market = AssetTypes.IsSpot(assetConfig.Type) ? "spot" : "futures/um";
        var interval = feedConfig.Interval;
        var rows = new List<string[]>();
        var available = false;

        await using (var monthly = await archive.DownloadMonthly(market, dataset, assetConfig.Symbol, interval, year, month, ct))
        {
            if (monthly is not null)
            {
                using var reader = new StreamReader(monthly);
                rows.AddRange(ArchiveCsv.ReadRows(reader));
                available = true;
            }
        }

        if (!available)
        {
            // Closed months only (ownership rule) — no "clamp to today" needed; the caller
            // never passes the current month. TODO: parallelize daily downloads within
            // ArchiveDownloadConcurrency if 31 sequential round-trips prove slow.
            var monthStart = new DateOnly(year, month, 1);
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);

            for (var day = monthStart; day <= monthEnd; day = day.AddDays(1))
            {
                await using var daily = await archive.DownloadDaily(market, dataset, assetConfig.Symbol, interval, day, ct);
                if (daily is null) continue;
                using var reader = new StreamReader(daily);
                rows.AddRange(ArchiveCsv.ReadRows(reader));
                available = true;
            }
        }

        if (!available)
            return new ArchiveMonthResult(0, AvailableAtSource: false);

        var fromMs = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(new DateOnly(year, month, 1).AddMonths(1), TimeOnly.MinValue, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var parsed = rows
            .Select(r => (Ts: ArchiveCsv.NormalizeTimestampMs(long.Parse(r[0], CultureInfo.InvariantCulture)), Row: r))
            .Where(x => x.Ts >= fromMs && x.Ts < toMs)
            .OrderBy(x => x.Ts)
            .ToList();

        if (parsed.Count == 0)
        {
            // Archive HAD the file(s) but nothing landed in-range — distinct from a 404;
            // report available so job diagnostics don't misread "present but empty" as "absent".
            logger.LogWarning("{Dataset} {Symbol} {Year}-{Month:D2}: archive present but 0 in-range rows",
                dataset, assetConfig.Symbol, year, month);
            return new ArchiveMonthResult(0, AvailableAtSource: true);
        }

        long written = feedName == FeedNames.Candles
            ? await WriteCandles(assetConfig, assetDir, interval, year, month, parsed, ct)
            : await WriteMarkPrice(assetDir, interval, year, month, parsed, ct);

        await MergeStatus(assetDir, feedName == FeedNames.Candles ? FeedNames.Candles : FeedNames.MarkPrice,
            interval, parsed[0].Ts, parsed[^1].Ts, written, ct);
        if (feedName == FeedNames.Candles && AssetTypes.IsFutures(assetConfig.Type))
            await MergeStatus(assetDir, FeedNames.CandleExt, interval, parsed[0].Ts, parsed[^1].Ts, written, ct);

        logger.LogInformation("Materialized {Feed}/{Interval} {Year}-{Month:D2} for {Symbol}: {Rows} rows",
            feedName, interval, year, month, assetConfig.Symbol, written);
        return new ArchiveMonthResult(written, AvailableAtSource: true);
    }

    // WriteCandles: EnsureCandleConfig; build candle rows + (futures) ext rows; two ReplacePartition calls.
    // WriteMarkPrice: EnsureSchema(["o","h","l","c"]); one ReplacePartition call.
    // MergeStatus: load, min/max first/last, RecordCount += written, Save.
    // Partition paths: candles → Path.Combine(assetDir, "candles", $"{year:D4}-{month:D2}_{interval}.csv")
    //                  others  → Path.Combine(assetDir, feed,      $"{year:D4}-{month:D2}_{interval}.csv")
    // Candle scaling: var multiplier = (decimal)Math.Pow(10, assetConfig.DecimalDigits);
    //                 MoneyConvert.ToLong(decimal.Parse(row[1], CultureInfo.InvariantCulture) * multiplier) etc.
    // Ext proxy: vol = double row[5]; tc = double row[8]; tb = double row[9];
    //            proxy = vol > 0 ? Math.Clamp(tc * tb / vol, 0, tc) : 0;
    // Double rendering: v.ToString(CultureInfo.InvariantCulture).
}
```

Write the three private methods in full (they are mechanical; the contract above pins every value). The ONLY subtle piece is proxy clamping and invariant-culture parsing/rendering — both covered by tests 2 and 1.

- [ ] **Step 4: Run → PASS.** Leave unstaged.

---

### Task 6: MetricsArchiveMaterializer (OI + 3×LS-ratio)

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/MetricsArchiveMaterializer.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Archive/MetricsArchiveMaterializerTests.cs`

**Interfaces:**
- Consumes: same as Task 5.
- Produces: FOUR `IArchiveMaterializer` registrations from ONE class parameterized by target feed:

```csharp
public MetricsArchiveMaterializer(
    string feedName,   // FeedNames.OpenInterest | LsRatioGlobal | LsRatioTopAccounts | LsRatioTopPositions
    IBinanceArchiveClient archive,
    IPartitionFileWriter partitionWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<MetricsArchiveMaterializer> logger)
```

**Behavior contract:**
1. `Exchange => "binance"`; `Supports(assetType) => AssetTypes.IsFutures(assetType)` (metrics is futures-only).
2. `metrics` is **daily-only** — always assemble the month from daily zips (no monthly attempt). Missing days do NOT fail the call. Gaps are NOT synthesized from day boundaries: after parsing/downsampling, detect them from the actual row sequence via jump detection exactly like `FeedCollectorBase.DetectGap` — `FromMs = previousPresentTs, ToMs = currentPresentTs` when the jump exceeds `intervalMs * feedConfig.GapThresholdMultiplier`. This matches the repo-wide `DataGap` convention (both ends present rows, missing = span/interval − 1), makes Task 7's gap credit arithmetic exact (a missing 5m day = 288 missing rows = a 24h05m present-to-present span → 289 − 1 = 288 credited), and naturally merges consecutive missing days into one gap. Dedup against existing status gaps by exact `(FromMs, ToMs)` equality before appending. Also add the one-line doc comment to `DataGap` in `src/AlgoTradeForge.HistoryLoader.Domain/FeedStatus.cs` pinning this convention (`/// <summary>Both ends are present rows: FromMs = last row before the hole, ToMs = first row after it.</summary>`).
3. `create_time` parsing: `DateTime.ParseExact(row[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)` → `new DateTimeOffset(dt).ToUnixTimeMilliseconds()`. (NOT an epoch number — this dataset is the odd one out.)
4. Downsampling: keep rows where `ts % intervalMs == 0` with `intervalMs = (long)IntervalParser.ToTimeSpan(feedConfig.Interval).TotalMilliseconds` (source cadence is 5m; configured feeds are 5m/15m/1h so alignment selection is exact).
5. Column mapping per target feed (metrics indices: 2=sum_open_interest, 3=sum_open_interest_value, 4=count_toptrader_long_short_ratio, 5=sum_toptrader_long_short_ratio, 6=count_long_short_ratio, 7=sum_taker_long_short_vol_ratio):
   - `open-interest`: columns `["oi","oi_usd"]`, values `[row2, row3]`.
   - `ls-ratio-global`: columns `["long_pct","short_pct","ratio"]`, ratio = row6, `long_pct = r/(1+r)`, `short_pct = 1/(1+r)` (fractions, matching the REST collector's semantics).
   - `ls-ratio-top-accounts`: same shape, ratio = row4.
   - `ls-ratio-top-positions`: same shape, ratio = row5.
6. Partition path `{assetDir}/{feedName}/{yyyy-MM}_{interval}.csv`; `EnsureSchema` with the feed's columns; status merge as in Task 5 plus the new gaps.
7. Return `ArchiveMonthResult(rowsWritten, availableAtSource: any day succeeded)`.

- [ ] **Step 1: Failing tests** — fixture:

```csharp
private const string MetricsCsv =
    "create_time,symbol,sum_open_interest,sum_open_interest_value,count_toptrader_long_short_ratio,sum_toptrader_long_short_ratio,count_long_short_ratio,sum_taker_long_short_vol_ratio\n" +
    "2024-03-01 00:00:00,BTCUSDT,108532.354,6370849179.8,2.96564793,1.303872,2.84772561,1.27027\n" +
    "2024-03-01 00:05:00,BTCUSDT,108533.926,6363680536.55,2.96809221,1.303266,2.85246656,0.654691\n" +
    "2024-03-01 00:15:00,BTCUSDT,108465.299,6358363944.54,2.96854526,1.301941,2.85154501,0.517488\n";
```

Tests:
1. `OpenInterest_WritesOiRows_AtConfiguredInterval` — interval "15m" → rows at 00:00 and 00:15 only (00:05 dropped); values `108532.354,6370849179.8` / `108465.299,6358363944.54`; header `ts,oi,oi_usd`.
2. `LsRatioGlobal_DerivesPctFromRatio` — interval "5m", first row ratio 2.84772561 → `long_pct = 2.84772561/3.84772561 ≈ 0.740105...`, `short_pct = 1/3.84772561 ≈ 0.259894...` — assert with `Assert.Equal(expected, actual, precision)` on parsed doubles from the written row (parse the CSV back rather than string-compare floats).
3. `MissingDay_RecordsPresentToPresentGap_AndContinues` — day 1 returns CSV (last row `2024-03-01 23:55:00`), day 2 null, day 3 returns CSV (first row `2024-03-03 00:00:00`), remaining days null. Assert saved status contains a `DataGap` with `FromMs` = ts of `2024-03-01 23:55:00` and `ToMs` = ts of `2024-03-03 00:00:00` (present-to-present, NOT day boundaries), and `result.AvailableAtSource` is true. Cross-check the credit arithmetic: for a 5m feed this gap spans 24h05m → `289 − 1 = 288` missing rows, exactly one day's worth.
4. `AllDaysMissing_ReportsUnavailable`.
5. `TopAccounts_And_TopPositions_MapCorrectColumns` — ratio column 4 vs 5 respectively.
6. `RejectsSpot` — `Supports(AssetTypes.Spot)` false.

- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement** per contract (structure mirrors Task 5: gather day streams → parse → filter/downsample → build rows → `ReplacePartition` → status merge with gaps).
- [ ] **Step 4: Run → PASS.** Leave unstaged.

---

### Task 7: MonthCoverageCalculator

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Archive/IMonthCoverageCalculator.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/MonthCoverageCalculator.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Archive/MonthCoverageCalculatorTests.cs`

**Interfaces:**
- Produces:

```csharp
public interface IMonthCoverageCalculator
{
    // Interval feeds only in phase 1 (ticks' CompleteMonths marker is phase 3).
    // Covered iff partition exists AND actualRows + gapRows >= expectedRows for the month
    // (expected clamped to "now" for the current month — which therefore is never covered
    // unless now is past month end). gaps = recorded source DataGaps from FeedStatus.
    Task<bool> IsMonthCovered(
        string assetDir, string feedName, string interval,
        int year, int month,
        IReadOnlyList<DataGap> gaps,
        CancellationToken ct = default);
}
```
- Constructor: `MonthCoverageCalculator(TimeProvider clock)` — inject `TimeProvider` so tests pin "now" (use existing `TestClock`).

**Computation:**
- `intervalMs = (long)IntervalParser.ToTimeSpan(interval).TotalMilliseconds`.
- `monthStartMs/monthEndMs` as UTC month bounds; `effectiveEndMs = min(monthEndMs, clock.GetUtcNow().ToUnixTimeMilliseconds())`. If `effectiveEndMs <= monthStartMs` → false.
- `expectedRows = (effectiveEndMs - monthStartMs) / intervalMs` (integer division; candles are open-time stamped so a fully closed month of 1h has `daysInMonth*24` rows).
- `actualRows = File.ReadLines(partitionPath).Count() - 1` (minus header), 0/false if file missing. Partition path: candles feed uses `candles/{yyyy-MM}_{interval}.csv`, other feeds `{feedName}/{yyyy-MM}_{interval}.csv` (accept `feedName` as the directory — callers pass "candles" for candles).
- `gapRows = Σ over gaps` of `max(0, (min(g.ToMs, effectiveEndMs) − max(g.FromMs, monthStartMs)) / intervalMs − 1)` — a `DataGap` spans from the last present row to the next present row, so the missing rows strictly inside are `span/interval − 1`.
- Covered iff `actualRows + gapRows >= expectedRows`.

- [ ] **Step 1: Failing tests** — temp-dir fixture like Task 4; `TestClock` pinned to `2026-07-07T00:00:00Z`:
1. `MissingPartition_NotCovered`.
2. `FullPastMonth_Covered` — write a synthetic 1h partition for 2024-03 with header + 744 rows (`31*24`); assert covered. Generate rows in the test with a loop.
3. `PartialTail_NotCovered` — 700 rows.
4. `HoleInMiddle_NotCovered` — 744 rows minus 10 removed from the middle (734 lines) → not covered (this is the head/middle-hole guard from the spec review).
5. `SourceGap_CountsTowardCoverage` — 720 rows + one `DataGap` spanning 25h (24 missing rows: `25*3600_000` span → `25 − 1 = 24` gap rows) → covered.
6. `CurrentMonth_NeverCovered` — clock inside the month, full file → expected keeps growing; write rows only up to "now" → `actualRows == expectedRows` boundary: assert NOT covered when rows end one interval before now, covered-equal case acceptable — pin the test to rows strictly fewer than expected (e.g. clock at 00:30, one row) → not covered.

- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement** per computation block (single small class).
- [ ] **Step 4: Run → PASS.** Leave unstaged.

---

### Task 8: ArchiveBackfillService + SymbolCollector archive-first branch

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Archive/ArchiveBackfillService.cs`
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Collection/SymbolCollector.cs` (ctor + `CollectFeedAsync`)
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Collection/BackfillOrchestrator.cs` (add optional `toDate` to `TryRunSingleAsync`)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Archive/ArchiveBackfillServiceTests.cs`
- Modify: `tests/AlgoTradeForge.HistoryLoader.Tests/Collection/SymbolCollectorTests.cs` (ctor change compile fix — pass a registry-less `ArchiveBackfillService` stub)

**Interfaces:**
- Produces:

```csharp
public sealed class ArchiveBackfillService(
    ArchiveMaterializerRegistry registry,
    IMonthCoverageCalculator coverage,
    ISettingsWriter settingsWriter,
    TimeProvider clock,
    ILogger<ArchiveBackfillService> logger)
{
    // Covers CLOSED whole months in [fromMs, toMs] from the archive for a replenishable feed.
    // Returns the REST-tail start: min(toMs, startOfCurrentMonthMs) after processing, or fromMs
    // unchanged when the feed is not replenishable. The current month is NEVER archive-touched
    // (ownership rule — see plan header).
    public Task<long> CoverFromArchive(
        AssetCollectionConfig assetConfig,
        FeedCollectionConfig feedConfig,
        string assetDir,
        long fromMs, long toMs,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default);
}
public readonly record struct ArchiveProgress(int MonthsDone, int MonthsTotal, string CurrentMonth);
```

**Algorithm (CoverFromArchive):**
1. `materializer = registry.Resolve(assetConfig.Exchange, feedConfig.Name, assetConfig.Type)`; null → return `fromMs`.
2. `currentMonthStartMs` = first instant of the current UTC month per `clock`. Candidate months = every (year,month) fully inside `[fromMs, min(toMs, currentMonthStartMs))` — i.e. **closed months only**; the current month is never a candidate. If the candidate set is empty → return `fromMs`.
3. Load `FeedStatus` once for gaps. Skip months where `await coverage.IsMonthCovered(assetDir, DirFor(feedConfig), feedConfig.Interval, y, m, status.Gaps, ct)` is true. (No forced re-materialization of the newest closed month: the coverage predicate itself decides — a complete closed month stays skipped, an incomplete one is re-covered. "Most recent partition re-check" from spec §2 is thereby a *re-check*, not a re-download.)
4. Materialize remaining months oldest→newest, reporting progress. Months with `AvailableAtSource == false` **before the first available month** advance an `earliestAvailable` cursor; after the loop, when the cursor moved and the asset is a CONFIGURED one, persist it via `settingsWriter.UpdateFeedHistoryStart(assetConfig.Symbol, assetConfig.Type, feedConfig.Name, feedConfig.Interval, discoveredDate, ct)` (same mechanism `SymbolCollector` uses after REST date discovery; skip for synthesized assets). Unavailability after data has started is just skipped (intra-month source gaps are recorded by the materializer).
5. Return `min(toMs, currentMonthStartMs)` clamped to `>= fromMs` — the REST tail always owns `[currentMonthStart, toMs]`, so eager collection keeps refreshing today's data exactly as before this feature.

`SymbolCollector` changes:
- Ctor gains `ArchiveBackfillService archiveBackfill` parameter.
- At the top of `CollectFeedAsync` after the collector lookup and spot-support check:

```csharp
fromMs = await archiveBackfill.CoverFromArchive(assetConfig, feedConfig, assetDir, fromMs, toMs, progress: null, ct);
if (fromMs >= toMs)
    return; // fully covered by archive — no REST tail needed
```

`BackfillOrchestrator.TryRunSingleAsync` gains `DateOnly? toDate = null` (before `ct`); `toMs = toDate is { } d ? new DateTimeOffset(d.AddDays(1), TimeOnly.MinValue, TimeSpan.Zero).ToUnixTimeMilliseconds() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`. Existing callers unchanged (optional param).

- [ ] **Step 1: Failing tests** (NSubstitute for registry input materializer, `IMonthCoverageCalculator`, `ISettingsWriter`; `TestClock` pinned to `2026-07-07T00:00:00Z`):
1. `NotReplenishable_ReturnsFromMsUnchanged_AndMaterializesNothing`.
2. `CoversMissingClosedMonths_OldestFirst` — range 2026-03-01..2026-07-07, coverage: April covered, March/May/June not → materializer called for March, May, June in that order; NOT for July.
3. `CurrentMonth_NeverArchiveTouched_ReturnStartOfCurrentMonth` — same range → returned ts == `2026-07-01T00:00:00Z` in ms (REST owns the tail from there); assert no materializer call with `(2026, 7)`.
4. `RangeEntirelyInClosedMonths_ReturnsToMs` — range 2026-01-01..2026-03-01 (to < current month start) → returned ts == toMs.
5. `RangeEntirelyInCurrentMonth_ReturnsFromMsUnchanged_NoMaterialization` — range 2026-07-02..2026-07-07 → no candidates, return fromMs.
6. `UnavailableLeadingMonths_PersistDiscoveredStart_ForConfiguredAsset` — months 1–2 unavailable, month 3 materializes → `settingsWriter.UpdateFeedHistoryStart` called once with month 3's first day.
7. `CoveredClosedMonth_Skipped` — coverage true for a complete closed month → materializer not called for it (no forced re-download).

- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement**; fix `SymbolCollectorTests` compile (add the new ctor arg — construct a real `ArchiveBackfillService` with `new ArchiveMaterializerRegistry([])`, NSubstitute stubs for `IMonthCoverageCalculator`/`ISettingsWriter`, a `TestClock`, and `NullLogger` — the empty registry makes it a guaranteed no-op).
- [ ] **Step 4: Run the FULL suite** (`dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/`): PASS. Leave unstaged.

---

### Task 9: Load jobs (registry + queue + worker)

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Archive/Jobs/LoadJob.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Archive/Jobs/LoadJobState.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Archive/Jobs/LoadJobRecord.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Archive/Jobs/LoadJobSnapshot.cs` (multi-line record → own file per one-type-per-file)
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Archive/Jobs/ILoadJobRegistry.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Archive/Jobs/LoadJobRegistry.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.WebApi/Collection/LoadJobWorker.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Archive/LoadJobRegistryTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record LoadJob(
    string JobId,          // Guid.NewGuid().ToString("N") minted by the endpoint
    string Exchange, string Symbol, string AssetType,
    string FeedName, string Interval,
    DateOnly From, DateOnly To);

public enum LoadJobState { Queued, Running, Complete, Error }

public sealed class LoadJobRecord
{
    public required LoadJob Job { get; init; }
    public required DateTimeOffset QueuedAt { get; init; }
    public LoadJobState State { get; set; } = LoadJobState.Queued;
    public int MonthsDone { get; set; }
    public int MonthsTotal { get; set; }
    public string? CurrentMonth { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public LoadJobSnapshot Snapshot(); // lock-protected copy
}
public sealed record LoadJobSnapshot(
    string JobId, LoadJobState State, DateTimeOffset QueuedAt, DateTimeOffset? CompletedAt,
    int MonthsDone, int MonthsTotal, string? CurrentMonth, string? ErrorCode, string? ErrorMessage,
    string Symbol, string FeedName, string Interval, DateOnly From, DateOnly To);

public interface ILoadJobRegistry
{
    // FeedKey = $"{assetDir}|{feedName}|{interval}". One active job per feed key.
    LoadEnqueueOutcome TryEnqueue(LoadJob job, string feedKey);
    LoadJobSnapshot? Get(string jobId);
    string? ActiveJobForSymbol(string assetDir);      // 409 payload for symbol-level conflicts
    LoadJob? Dequeue(CancellationToken ct);           // blocking channel read used by the worker (async: Task<LoadJob?> DequeueAsync-shape but NO Async suffix: Task<LoadJob?> Dequeue(CancellationToken ct))
    void OnStarted(string jobId);
    void OnProgress(string jobId, int monthsDone, int monthsTotal, string currentMonth);
    void OnCompleted(string jobId);
    void OnErrored(string jobId, string code, string message);
}
public abstract record LoadEnqueueOutcome
{
    public sealed record Accepted(LoadJobRecord Record) : LoadEnqueueOutcome;
    public sealed record FeedBusy(string ActiveJobId) : LoadEnqueueOutcome;
    public sealed record QueueFull : LoadEnqueueOutcome;
}
```
- Implementation notes: internal `Channel<LoadJob>` bounded by new option `LoadOptions.MaxQueueDepth` (add `public LoadOptions Load { get; init; } = new();` to `HistoryLoaderOptions` with `public sealed class LoadOptions { public int MaxQueueDepth { get; init; } = 16; public int JobRetentionMinutes { get; init; } = 30; public int MaxMonthsPerRequest { get; init; } = 600; }`). Keep the registry a simplified sibling of `AggregationJobRegistry` (ConcurrentDictionary by jobId + active-by-feedKey + active-by-assetDir index; lazy retention eviction in `Get`); inject `TimeProvider`. NO SSE/event log — snapshot polling only in phase 1.
- `Dequeue` signature: `Task<LoadJob?> Dequeue(CancellationToken ct)` — returns null on channel close.

`LoadJobWorker` (WebApi, `BackgroundService`): loop `await registry.Dequeue(stoppingToken)`; for each job: resolve/synthesize `AssetCollectionConfig` (Task 10's resolver — in THIS task inject a `Func` seam: the worker takes `ILoadAssetResolver` defined in Task 10; to keep Task 9 self-contained define the interface here):

```csharp
public interface ILoadAssetResolver
{
    // Returns the configured asset or synthesizes one (resolving DecimalDigits) for unknown symbols.
    Task<AssetCollectionConfig> Resolve(string exchange, string symbol, string assetType, CancellationToken ct = default);
}
```

Worker body per job:

```csharp
registry.OnStarted(job.JobId);
try
{
    var asset = await assetResolver.Resolve(job.Exchange, job.Symbol, job.AssetType, ct);
    var assetDir = BackfillOrchestrator.ResolveAssetDir(options.CurrentValue.DataRoot, asset);
    var ok = await orchestrator.TryRunSingleAsync(
        asset, assetDir, feedFilter: [job.FeedName], fromDate: job.From, toDate: job.To, ct);
    if (ok) registry.OnCompleted(job.JobId);
    else registry.OnErrored(job.JobId, "symbol_busy", "Another backfill holds the symbol lock; retry later.");
}
catch (ArchiveIntegrityException ex) { registry.OnErrored(job.JobId, "checksum_mismatch", ex.Message); }
catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
{ registry.OnErrored(job.JobId, "load_failed", ex.Message); }
```

Before writing the catch filter, check the existing WebApi stream services for the established shutdown-filter helper (grep `IsTrueShutdown` in `src/AlgoTradeForge.HistoryLoader.WebApi/`) and use it if present — the inline `!(ex is OperationCanceledException && ct.IsCancellationRequested)` form above is the fallback, not the preference.

Caveat: `TryRunSingleAsync` runs the feed with the asset's `FeedCollectionConfig`; for a synthesized asset the resolver must include a `FeedCollectionConfig { Name = job.FeedName, Interval = job.Interval }` in `asset.Feeds`. For a CONFIGURED asset whose config lacks the requested interval, the resolver appends a transient `FeedCollectionConfig` too (do not persist to appsettings).

- [ ] **Step 1: Failing tests** (registry only; worker is thin glue covered by endpoint tests in Task 10):
1. `TryEnqueue_Accepts_AndGetReturnsSnapshot`.
2. `SecondEnqueue_SameFeedKey_ReturnsFeedBusy_WithActiveJobId`.
3. `Enqueue_AfterTerminal_Accepts` (complete the first via OnCompleted).
4. `QueueFull_ReturnsQueueFull` (MaxQueueDepth=1, enqueue 2 distinct feed keys).
5. `Get_EvictsTerminal_PastRetention` (TestClock advance beyond JobRetentionMinutes).
6. `ActiveJobForSymbol_ReturnsActiveJobId_ForSameAssetDir`.

- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement** registry + worker (mirror `AggregationJobRegistry` locking discipline; single per-feedKey lock object via `ConcurrentDictionary<string, object>`).
- [ ] **Step 4: Run → PASS.** Leave unstaged.

---

### Task 10: Endpoints + asset resolver + DI wiring

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/BinanceLoadAssetResolver.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/LoadEndpoints.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/CoverageEndpoints.cs`
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/DependencyInjection.cs` (register everything below)
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs` (map endpoint groups, add `LoadJobWorker` hosted service)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Archive/LoadEndpointValidationTests.cs`, `tests/AlgoTradeForge.HistoryLoader.Tests/Archive/BinanceClassificationTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces HTTP contract:

```
POST /api/v1/loads
  body: { "exchange": "binance", "symbol": "LTCUSDT", "assetType": "perpetual",
          "feedName": "open-interest", "interval": "5m", "from": "2020-09-01", "to": "2024-01-01" }
  202 → { "jobId": "..." }
  409 → { "error": "feed_busy" | "symbol_busy", "activeJobId": "..." }
  422 → { "error": "not_replenishable" | "invalid_range" | "too_many_months" | "unknown_asset_type", "message": "..." }
  503 → { "error": "queue_full" }
GET  /api/v1/loads/{jobId}
  200 → LoadJobSnapshot JSON | 404
GET  /api/v1/coverage?exchange=binance&symbol=BTCUSDT&assetType=perpetual
  200 → { "assetDir": "...", "feeds": [ { "feedName": "candles", "interval": "1h",
          "coveredMonths": ["2024-01","2024-02"], "firstTimestamp": 0, "lastTimestamp": 0 } ] }
```

**Validation order in POST (write as small static helpers):** unknown assetType (`AssetTypes` parse) → 422; `from > to` → 422; months span > `LoadOptions.MaxMonthsPerRequest` → 422; `registry.IsReplenishable(exchange, feedName, assetType)` false → 422 `not_replenishable`; symbol-level `loadRegistry.ActiveJobForSymbol(assetDir)` → 409 `symbol_busy`; enqueue outcome FeedBusy → 409, QueueFull → 503, Accepted → 202.

**`BinanceLoadAssetResolver : ILoadAssetResolver`:**
- The resolver is **feed-agnostic**: configured symbol (match `options.CurrentValue.Assets` by Symbol+Type) → return the configured `AssetCollectionConfig` as-is; unknown symbol → synthesize (below). The WORKER (Task 9), after `Resolve`, appends a transient `FeedCollectionConfig { Name = job.FeedName, Interval = job.Interval }` to `asset.Feeds` when no matching entry exists — never persisted to appsettings.
- Unknown symbol → `GET {SpotBaseUrl}/api/v3/exchangeInfo?symbol=X` (spot) or `{FuturesBaseUrl}/fapi/v1/exchangeInfo?symbol=X` (futures) via `IHttpClientFactory.CreateClient("binance-archive")`; parse `symbols[0].filters[?filterType=="PRICE_FILTER"].tickSize` (string like `"0.01000000"`) → `DecimalDigits = -floor(log10(decimal.Parse(tickSize)))` computed as `BitConverter`-free: count digits after the decimal point up to the last non-zero char. Return `new AssetCollectionConfig { Symbol, Type, DecimalDigits, HistoryStart = new DateOnly(2017, 1, 1), Feeds = [] }`.
- Non-OK response → throw `InvalidOperationException($"exchangeInfo failed for {symbol}")` (job errors as `load_failed`).

**Coverage endpoint:** read `feeds.json` via `ISchemaManager.Load(assetDir, ct)`; for every feed entry that has an interval-style feed dir, enumerate partition files `{assetDir}/{feedDir}/????-??_{interval}.csv` and return the months whose `IsMonthCovered` is true, plus FeedStatus first/last. Keep it a thin projection — no new abstractions.

**DI registrations to add (Infrastructure `DependencyInjection.cs`, follow existing style):**

```csharp
services.AddHttpClient("binance-archive", (sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<HistoryLoaderOptions>>().Value;
    client.BaseAddress = new Uri(opts.Binance.ArchiveBaseUrl);
    client.Timeout = TimeSpan.FromMinutes(5);
});
services.AddSingleton<IBinanceArchiveClient, BinanceArchiveClient>();
services.AddSingleton<IPartitionFileWriter, PartitionFileWriter>();
services.AddSingleton<IMonthCoverageCalculator, MonthCoverageCalculator>();
services.AddSingleton<ILoadAssetResolver, BinanceLoadAssetResolver>();
services.AddSingleton<ILoadJobRegistry, LoadJobRegistry>();
services.AddSingleton<ArchiveBackfillService>();
services.AddSingleton<ArchiveMaterializerRegistry>();
// materializer set — THE classification (spec §1):
services.AddSingleton<IArchiveMaterializer>(sp => new KlinesArchiveMaterializer(
    FeedNames.Candles, "klines", supportsSpot: true,
    sp.GetRequiredService<IBinanceArchiveClient>(), sp.GetRequiredService<IPartitionFileWriter>(),
    sp.GetRequiredService<ISchemaManager>(), sp.GetRequiredService<IFeedStatusStore>(),
    sp.GetRequiredService<ILogger<KlinesArchiveMaterializer>>()));
services.AddSingleton<IArchiveMaterializer>(sp => new KlinesArchiveMaterializer(
    FeedNames.MarkPrice, "markPriceKlines", supportsSpot: false, /* same deps */));
services.AddSingleton<IArchiveMaterializer>(sp => new MetricsArchiveMaterializer(FeedNames.OpenInterest, /* deps */));
services.AddSingleton<IArchiveMaterializer>(sp => new MetricsArchiveMaterializer(FeedNames.LsRatioGlobal, /* deps */));
services.AddSingleton<IArchiveMaterializer>(sp => new MetricsArchiveMaterializer(FeedNames.LsRatioTopAccounts, /* deps */));
services.AddSingleton<IArchiveMaterializer>(sp => new MetricsArchiveMaterializer(FeedNames.LsRatioTopPositions, /* deps */));
```

(Expand `/* deps */` in the real edit — same five services as the first registration.)

**Program.cs:** `app.MapLoadEndpoints(); app.MapCoverageEndpoints();` next to the existing endpoint mappings; `builder.Services.AddHostedService<LoadJobWorker>();` next to the existing hosted services. If `TimeProvider` isn't registered yet, add `services.TryAddSingleton(TimeProvider.System);`.

- [ ] **Step 1: Failing tests**

`BinanceClassificationTests` — construct the REAL materializer set (with NSubstitute deps) exactly as DI does and assert the spec's classification table:

```csharp
[Fact] public void Candles_Spot_Replenishable() ...
[Fact] public void Candles_Perpetual_Replenishable() ...
[Fact] public void MarkPrice_Spot_NotReplenishable() ...
[Fact] public void OpenInterest_Perpetual_Replenishable() ...
[Fact] public void OpenInterest_Spot_NotReplenishable() ...
[Fact] public void Liquidations_NotReplenishable() ...
[Fact] public void UnknownExchange_Ib_NotReplenishable() ...
```

`LoadEndpointValidationTests` — extract the POST validation into a testable static `LoadRequestValidator.Validate(request, registry, options) : LoadValidationError?` and test: unknown asset type, from>to, months-cap exceeded, not_replenishable, happy path null.

- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement** endpoints (follow `BackfillEndpoints`/`AggregationEndpoints` style: static class, `MapGroup("/api/v1")`, request/response records in the same file as the endpoint class is acceptable per existing convention — `BackfillRequest`/`BackfillResponse` precedent), resolver, DI, Program wiring.
- [ ] **Step 4: Run the FULL HistoryLoader suite → PASS.**
- [ ] **Step 5: Build the whole solution:** `dotnet build AlgoTradeForge.slnx` → 0 warnings / 0 errors. Leave unstaged.

---

### Task 11: Live smoke test (manual, optional but recommended)

**Steps:**
- [ ] Run the WebApi locally (`dotnet run --project src/AlgoTradeForge.HistoryLoader.WebApi` — NOT while the Windows service is running on port 5210; use `--urls http://localhost:5211` to avoid the port clash).
- [ ] `POST http://localhost:5211/api/v1/loads` with `{ "exchange":"binance", "symbol":"BTCUSDT", "assetType":"perpetual", "feedName":"open-interest", "interval":"5m", "from":"2024-01-01", "to":"2024-02-01" }`.
- [ ] Poll `GET /api/v1/loads/{jobId}` until Complete; verify `{DataRoot}/binance/BTCUSDT_perp/open-interest/2024-01_5m.csv` exists with ~8928 rows (31×288) and `GET /api/v1/coverage?...` reports 2024-01 covered.
- [ ] Repeat for `feedName: "candles", interval: "1h"` on a spot symbol to exercise the spot path.

**Report results to Andrew; he commits.**

---

## Self-Review (updated after external review, 2026-07-07)

- **Spec coverage (phase 1):** classification model (Tasks 2, 10), BinanceArchiveClient + quirks + transient backoff (Tasks 1, 3), klines/mark-price/metrics materializers (Tasks 5, 6), completeness-aware coverage incl. head/middle holes + gap credit (Task 7), archive-first branch + earliest-available persistence via `ISettingsWriter` (Task 8), /api/v1/loads + /api/v1/coverage + job registry + any-symbol synthesis + months cap + 409-with-job-id (Tasks 9, 10). Phase 2/3 items (lazy flip, UI, aggTrades, 1s, taker-volume switch, `CompleteMonths`) intentionally absent.
- **Review findings incorporated:**
  1. `DataGap` semantics unified repo-wide (both ends = present rows); Task 6 derives gaps from parsed-row jump detection, and the Global Constraints pin the convention so Tasks 6 and 7 cannot diverge again. A doc comment lands on `DataGap` itself.
  2. **Closed-months ownership rule** (header + Task 8): the archive never touches the current month; `CoverFromArchive` returns `min(toMs, currentMonthStart)`, so the REST tail runs every eager cycle exactly as today. This simultaneously removes the `BufferedPartitionWriter` ratchet-watermark race (closed months are never active append targets) and the daily 31-zip re-download cost.
  3. Constitution v1.9.1 (`LockAsync`), xUnit1051 (`TestContext.Current.CancellationToken`), one-type-per-file for `LoadJobSnapshot`, `IsTrueShutdown`-style catch preference, resolver/worker responsibility split resolved to a single directive.
- **Type consistency:** `IArchiveMaterializer.MaterializeMonth` signature identical in Tasks 2/5/6/8; `ArchiveMonthResult(RowsWritten, AvailableAtSource)` consistent (present-but-empty months report `AvailableAtSource: true`); `ILoadJobRegistry` consumed by Task 10 matches Task 9; `ILoadAssetResolver` defined once (Task 9), implemented feed-agnostic in Task 10.
- **Known accepted risks:** long jobs hold the per-symbol lock (spec §5 trade-off); `RecordCount` on FeedStatus becomes approximate after re-materialization (coverage math reads files, not FeedStatus); sequential daily downloads within a month (TODO in Task 5).
