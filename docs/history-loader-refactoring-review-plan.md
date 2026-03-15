# History Loader — Post-Review Fix Plan

Fixes for issues identified in the code review of branch `019-history-loader`.

---

## 1. HIGH — Thread-Safe Dedup Dictionaries in Singleton Writers

**Problem:** `CandleCsvWriter` and `FeedCsvWriter` are registered as singletons and hold a
mutable `Dictionary<string, long>` for dedup. Six `BackgroundService` collectors run concurrently
and share these singletons — `Dictionary<K,V>` is not thread-safe.

**Files:**
- `src/AlgoTradeForge.HistoryLoader.Infrastructure/Storage/CandleCsvWriter.cs` (line 9)
- `src/AlgoTradeForge.HistoryLoader.Infrastructure/Storage/FeedCsvWriter.cs` (line 9)

**Fix:**
Replace `Dictionary<string, long>` with `ConcurrentDictionary<string, long>` in both files.

```csharp
// Before
private readonly Dictionary<string, long> _lastWrittenTimestamps = new();

// After
private readonly ConcurrentDictionary<string, long> _lastWrittenTimestamps = new();
```

Update write access pattern:
```csharp
// Before (CandleCsvWriter line 49, FeedCsvWriter line 45)
_lastWrittenTimestamps[dedupKey] = record.TimestampMs;

// After — use AddOrUpdate for thread safety
_lastWrittenTimestamps.AddOrUpdate(dedupKey, record.TimestampMs, (_, _) => record.TimestampMs);
```

Update read access pattern (dedup check):
```csharp
// Before (CandleCsvWriter line 15, FeedCsvWriter line 20)
if (_lastWrittenTimestamps.TryGetValue(dedupKey, out var lastTs) && record.TimestampMs <= lastTs)
    return;

// After — TryGetValue is already thread-safe on ConcurrentDictionary, no change needed
```

Add `using System.Collections.Concurrent;` to both files.

---

## 2. MEDIUM — Incomplete Resume Optimization in CollectGenericFeedAsync

**Problem:** `CollectCandlesAsync` and `CollectFundingRateAsync` adjust `fromMs` forward when a
resume timestamp exists, skipping already-fetched data at the API level. `CollectGenericFeedAsync`
only filters records at the write side, still fetching them from the API — wasting quota.

**File:** `src/AlgoTradeForge.HistoryLoader.Application/Collection/SymbolCollector.cs`

**Fix — CollectGenericFeedAsync (around line 346):**
```csharp
// Before
var resumeTs = feedWriter.ResumeFrom(assetDir, feedName, interval);

// After — adjust fetch window to avoid redundant API calls
var resumeTs = feedWriter.ResumeFrom(assetDir, feedName, interval);
if (resumeTs.HasValue && resumeTs.Value >= fromMs)
    fromMs = resumeTs.Value + 1;
```

The write-side dedup (`if (resumeTs.HasValue && record.TimestampMs <= resumeTs.Value) continue;`)
can remain as a safety net but will rarely trigger after this change.

Note: `CollectMarkPriceAsync` already has this optimization (line 272-275), so no change needed there.

---

## 3. MEDIUM — Replace Fragile HTTP Status String Matching with StatusCode

**Problem:** HTTP 400 and 418 errors are detected via `ex.Message.Contains("400")` /
`ex.Message.Contains("418")` — fragile across .NET versions. `HttpRequestException.StatusCode`
(available since .NET 5) is the idiomatic approach.

### 3a. HTTP 400 in SymbolCollector

**File:** `src/AlgoTradeForge.HistoryLoader.Application/Collection/SymbolCollector.cs` (line 100)

```csharp
// Before
catch (HttpRequestException ex) when (ex.Message.Contains("400"))

// After
catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
```

### 3b. HTTP 418 in all 6 collector services

HTTP 418 ("I'm a Teapot" / Binance IP ban) has no named `HttpStatusCode` enum member, so cast
the integer literal.

**Files (all in `src/AlgoTradeForge.HistoryLoader/Collection/`):**
- `KlineCollectorService.cs` (line 58)
- `FundingRateCollectorService.cs` (line 58)
- `OiCollectorService.cs` (line 58)
- `RatioCollectorService.cs` (line 64)
- `HourlyCollectorService.cs` (line 65)
- `LiquidationCollectorService.cs` (line 58)

```csharp
// Before
catch (HttpRequestException ex) when (ex.Message.Contains("418"))

// After
catch (HttpRequestException ex) when (ex.StatusCode == (System.Net.HttpStatusCode)418)
```

Add `using System.Net;` to each file if not already present, then use unqualified `HttpStatusCode`.

---

## 4. MEDIUM — AutoApplySpec Type Validation

**Problem:** `AutoApplySpec.Type` is an unconstrained `string`. Invalid values like `"InvalidType"`
silently persist to `feeds.json` and only fail when the read side parses them.

**Files:**
- `src/AlgoTradeForge.HistoryLoader.Application/Abstractions/ISchemaManager.cs` (line 3)
- `src/AlgoTradeForge.HistoryLoader.Infrastructure/Storage/FeedSchemaManager.cs` (lines 35-42)

**Fix:** Add a static validation set and a factory method to `AutoApplySpec`:

```csharp
// In ISchemaManager.cs — update the record
public sealed record AutoApplySpec
{
    private static readonly HashSet<string> ValidTypes =
        ["FundingRate", "Dividend", "SwapRate"];

    public string Type { get; }
    public string RateColumn { get; }
    public string? SignConvention { get; }

    public AutoApplySpec(string type, string rateColumn, string? signConvention = null)
    {
        if (!ValidTypes.Contains(type))
            throw new ArgumentException($"Unknown auto-apply type: '{type}'. Valid: {string.Join(", ", ValidTypes)}", nameof(type));

        Type = type;
        RateColumn = rateColumn;
        SignConvention = signConvention;
    }
}
```

This keeps the decoupling from the Domain's `AutoApplyDefinition` while preventing invalid types
from reaching `feeds.json`.

---

## 5. LOW — Duplicated Asset Directory Naming Logic

**Problem:** `BackfillOrchestrator.ResolveAssetDir()` (Application) reimplements the `_fut` suffix
logic that also exists in `AssetDirectoryName.From()` (Infrastructure). If the naming convention
changes, both must be updated independently.

**File:** `src/AlgoTradeForge.HistoryLoader.Application/Collection/BackfillOrchestrator.cs` (lines 98-102)

**Fix:** Extract into a shared utility in the Domain layer (zero-dep, pure logic):

```csharp
// New file: src/AlgoTradeForge.HistoryLoader.Domain/AssetPathConvention.cs
namespace AlgoTradeForge.HistoryLoader.Domain;

public static class AssetPathConvention
{
    public static string DirectoryName(string symbol, string assetType)
    {
        var suffix = assetType is "perpetual" or "future" ? "_fut" : "";
        return $"{symbol}{suffix}";
    }
}
```

Then update `BackfillOrchestrator.ResolveAssetDir`:
```csharp
public static string ResolveAssetDir(string dataRoot, AssetCollectionConfig asset) =>
    Path.Combine(dataRoot, asset.Exchange, AssetPathConvention.DirectoryName(asset.Symbol, asset.Type));
```

And update `AssetDirectoryName.From` in Infrastructure to delegate to the same utility:
```csharp
public static string From(Asset asset) => asset switch
{
    CryptoPerpetualAsset a => AssetPathConvention.DirectoryName(a.Name, "perpetual"),
    FutureAsset a          => AssetPathConvention.DirectoryName(a.Name, "future"),
    CryptoAsset a          => AssetPathConvention.DirectoryName(a.Name, "spot"),
    EquityAsset a          => AssetPathConvention.DirectoryName(a.Name, "equity"),
    _ => throw new ArgumentException($"Unknown asset type: {asset.GetType().Name}")
};
```

---

## 6. LOW — Configurable Gap Detection Threshold

**Problem:** The `2x interval` gap threshold is hardcoded in 3 places in `SymbolCollector`.
Different feeds may warrant different thresholds.

**Files:**
- `src/AlgoTradeForge.HistoryLoader.Application/Collection/SymbolCollector.cs` (lines 187, 314, 380)
- `src/AlgoTradeForge.HistoryLoader.Application/HistoryLoaderOptions.cs`

**Fix:** Add a configurable multiplier to `HistoryLoaderOptions`:

```csharp
// In HistoryLoaderOptions.cs
public class HistoryLoaderOptions
{
    // ... existing fields ...
    public double GapDetectionMultiplier { get; set; } = 2.0;
}
```

Update `SymbolCollector` constructor to read the option and use it in all 3 gap-check sites:

```csharp
// Before (3 occurrences)
if (kline.TimestampMs - previousTs > expectedMs * 2)

// After
if (kline.TimestampMs - previousTs > expectedMs * gapMultiplier)
```

Where `gapMultiplier` is read from `IOptionsMonitor<HistoryLoaderOptions>` in the constructor
(SymbolCollector already has access to config options via its dependencies).

Wait — `SymbolCollector` currently does NOT inject `IOptionsMonitor<HistoryLoaderOptions>`.
The options are read by the collector services and passed as method parameters.

**Simpler approach:** Pass the multiplier as a field on `FeedCollectionConfig`:

```csharp
public class FeedCollectionConfig
{
    // ... existing fields ...
    public double GapThresholdMultiplier { get; set; } = 2.0;
}
```

Then use `feedConfig.GapThresholdMultiplier` in the gap checks. This allows per-feed tuning
via `appsettings.json`.

---

## 7. PACKAGE — Update Microsoft.Extensions.Http to Stable

**Problem:** `Microsoft.Extensions.Http` is pinned to a preview version (`10.0.0-preview.3.25171.5`).
The latest stable release is **10.0.3**.

**File:** `src/AlgoTradeForge.HistoryLoader.Infrastructure/AlgoTradeForge.HistoryLoader.Infrastructure.csproj` (line 15)

**Fix:**
```xml
<!-- Before -->
<PackageReference Include="Microsoft.Extensions.Http" Version="10.0.0-preview.3.25171.5" />

<!-- After -->
<PackageReference Include="Microsoft.Extensions.Http" Version="10.0.3" />
```

---

## Execution Order

| Step | Issue | Severity | Estimated Scope |
|------|-------|----------|-----------------|
| 1    | Thread-safe dictionaries (#1) | HIGH | 2 files, ~4 lines each |
| 2    | Package version update (#7) | PACKAGE | 1 file, 1 line |
| 3    | HTTP status code checks (#3) | MEDIUM | 7 files, 1 line each |
| 4    | Resume optimization (#2) | MEDIUM | 1 file, ~3 lines |
| 5    | AutoApplySpec validation (#4) | MEDIUM | 1 file, ~15 lines |
| 6    | Asset directory naming (#5) | LOW | 3 files, new utility |
| 7    | Gap detection threshold (#6) | LOW | 3 files, config change |

After all fixes: `dotnet build AlgoTradeForge.slnx && dotnet test src/AlgoTradeForge.HistoryLoader.Tests/`
