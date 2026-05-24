# FeedSchemaManager optimistic concurrency

**Date:** 2026-05-23
**Branch context:** `032-decouple-from-local-file-system`
**Scope:** `FeedSchemaManager` + `IFileStorage` + `LocalFileStorage`. Reader paths in
the same `IFileStorage`. No frontend changes. `AppSettingsWriter` left for a follow-up.

## Motivation

`FeedSchemaManager` today guards every public method — including pure reads — with a
per-`feeds.json`-path `SemaphoreSlim`. The lock was originally added to defend two
invariants:

1. **Writer / writer mutual exclusion** for read-modify-write sequences. Without this,
   two parallel writers each read pre-state, diverge their mutations, and the second
   writer clobbers the first. Real invariant. Must keep.
2. **Defensive serialization of readers vs. writers**, presumably motivated by Windows'
   `MoveFileEx(REPLACE_EXISTING)` failing when a reader holds an open handle that
   doesn't include `FileShare.Delete`. Real underlying concern, but the lock is the
   wrong primitive to fix it.

Symptom of the lock-on-reads: clicking the Data-tab "+" buttons rapidly raises
`OperationCanceledException` from `SemaphoreSlim.WaitUntilCountOrTimeoutAsync` inside
`FeedSchemaManager.Load`. Each click fires `GetAsset` → `BuildAssetEntries` →
parallel `Load` per asset, and TanStack-Query-driven `AbortSignal` cancellations
fire while requests are queued behind the gate. The user-visible result: "Loading
status…" stuck states in the side panel, and Visual Studio first-chance exception
breaks during dev.

## Design summary

* Drop the per-path `SemaphoreSlim` from `FeedSchemaManager` entirely.
* Introduce optimistic concurrency at the `IFileStorage` layer: a `ReadWithEtag`
  primitive that returns content + opaque ETag, and a `WriteIfMatch` primitive that
  conditionally writes iff the current ETag matches a caller-supplied expected value.
* `FeedSchemaManager` performs read-mutate-write loops with bounded retry
  (`MaxAttempts = 5`) and jittered exponential backoff (5–20 ms base, doubling).
* `LocalFileStorage` derives ETag as SHA-1 hex of the byte content. Writer-side CAS
  atomicity is preserved via an internal per-key `SemaphoreSlim` held only across
  the brief check-and-rename critical section; readers never touch it.
* All reader `FileStream` opens in `LocalFileStorage` add `FileShare.Delete`, so a
  writer's atomic rename can replace a file that a reader currently has open.
* `Load` keeps its `CancellationToken` parameter; cancellation is appropriate at the
  I/O layer (matters on S3 latency, useful for client-disconnect backpressure under
  load) — what was wrong was cancellation propagating into a contested gate, not
  cancellation existing on the read path.

## Architecture

```
FeedSchemaManager
  Load(assetDir, ct)                        — lock-free; passes ct to ReadWithEtag
  EnsureSchema / EnsureAltBarFeed / …       — all share UpdateWithRetry
  UpdateWithRetry(path, mutator, ct)        — read-mutate-write loop
        │
        ▼
IFileStorage
  StoredObject? ReadWithEtag(key, ct)
  string         WriteIfMatch(key, content, string? expectedEtag, ct)
                 // throws ConcurrencyConflictException on mismatch
        │
        ▼
LocalFileStorage  (this PR)            │ S3FileStorage  (future, out of scope)
  ETag = SHA-1 hex of bytes            │ ETag = S3 native (request header)
  Internal per-key SemaphoreSlim       │ Native If-Match conditional PutObject
  Atomic .tmp + File.Move(overwrite)   │ Native cross-process safety
  Readers: FileShare.ReadWrite|Delete  │
```

**Invariants this preserves:**

1. **Reads never block** on synchronization owned by `FeedSchemaManager`. Concurrent
   reads of the same `feeds.json` proceed in parallel. Concurrent reads alongside a
   writer's rename are safe because reader handles include `FileShare.Delete`.
2. **Writes are linearized per-key**, either via the local backend's writer-only mutex
   (single-process) or S3's native `If-Match` (multi-process). `FeedSchemaManager`
   doesn't care which.
3. **Atomic rename is unchanged.** `.tmp` + `File.Move(overwrite: true)` +
   `Flush(flushToDisk: true)` still bound the durability window.
4. **Retry policy lives in one place** — `FeedSchemaManager.UpdateWithRetry`. The
   storage layer is policy-free.

## Interface & types

In `AlgoTradeForge.Storage.Abstractions`:

```csharp
public sealed record StoredObject(string Content, string Etag);

public sealed class ConcurrencyConflictException(string key, string? expectedEtag, string? actualEtag)
    : Exception($"Concurrency conflict on '{key}': expected etag '{expectedEtag ?? "<absent>"}', actual '{actualEtag ?? "<absent>"}'.")
{
    public string Key { get; } = key;
    public string? ExpectedEtag { get; } = expectedEtag;
    public string? ActualEtag { get; } = actualEtag;
}
```

Additions to `IFileStorage`:

```csharp
/// <summary>
/// Reads the object at <paramref name="key"/> along with an opaque ETag. Returns
/// <c>null</c> when the key has no current object. The returned ETag is suitable
/// only for passing back to <see cref="WriteIfMatch"/>; its format is backend-
/// specific. Invariant: same bytes ⇒ same ETag.
/// </summary>
Task<StoredObject?> ReadWithEtag(string key, CancellationToken ct = default);

/// <summary>
/// Conditional atomic replace: writes <paramref name="content"/> iff the store's
/// current ETag for <paramref name="key"/> equals <paramref name="expectedEtag"/>.
/// Pass <c>null</c> for create-only semantics (succeeds iff the key has no current
/// object). Throws <see cref="ConcurrencyConflictException"/> on mismatch. Returns
/// the new ETag on success.
/// </summary>
Task<string> WriteIfMatch(string key, string content, string? expectedEtag, CancellationToken ct = default);
```

The existing methods (`ReadAllText`, `WriteAllText`, `OpenWriteSession`, etc.) are
unchanged — they remain the right tool for non-coordinated writes (e.g. CSV
partitions guarded by `WriteLockManager`).

## LocalFileStorage implementation

```csharp
private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new();

private SemaphoreSlim WriteLock(string fullPath) =>
    _writeLocks.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));

public async Task<StoredObject?> ReadWithEtag(string key, CancellationToken ct = default)
{
    var path = Resolve(key);
    if (!File.Exists(path)) return null;
    await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, DefaultBufferSize, useAsync: true);
    using var reader = new StreamReader(fs);
    var content = await reader.ReadToEndAsync(ct);
    return new StoredObject(content, EtagOf(content));
}

public async Task<string> WriteIfMatch(string key, string content, string? expectedEtag, CancellationToken ct = default)
{
    var path = Resolve(key);
    using var _ = await WriteLock(path).LockAsync(ct);

    var currentEtag = File.Exists(path)
        ? EtagOf(await File.ReadAllBytesAsync(path, ct))
        : null;
    if (currentEtag != expectedEtag)
        throw new ConcurrencyConflictException(key, expectedEtag, currentEtag);

    var dir = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    var tmp = path + ".tmp";
    await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                     FileShare.Read, DefaultBufferSize, useAsync: true))
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        await fs.WriteAsync(bytes, ct);
        await fs.FlushAsync(ct);
        fs.Flush(flushToDisk: true);
    }
    File.Move(tmp, path, overwrite: true);
    return EtagOf(content);
}

private static string EtagOf(string content) => EtagOf(Encoding.UTF8.GetBytes(content));
private static string EtagOf(ReadOnlySpan<byte> bytes)
{
    Span<byte> hash = stackalloc byte[20];
    SHA1.HashData(bytes, hash);
    return Convert.ToHexString(hash);
}
```

Existing reader paths (`OpenRead`, `ReadAllText`, `ReadLines`) also gain
`FileShare.Delete` in their `FileStream` flags.

## FeedSchemaManager rewrite

### Constants & helpers

```csharp
private const int MaxAttempts = 5;

private static TimeSpan Backoff(int attempt) =>
    TimeSpan.FromMilliseconds(Random.Shared.Next(5, 21) * (1 << attempt));
    // Backoff(0): 5–20 ms,  Backoff(1): 10–40,  Backoff(2): 20–80,  Backoff(3): 40–160
    // Backoff(4) is never reached — the 5th attempt has no following delay; on
    // its conflict the `when` guard fails and the exception propagates.
```

With `MaxAttempts = 5` there are exactly four delays between attempts. Worst-case
total wait: 20 + 40 + 80 + 160 = **300 ms**. Below any HTTP timeout; ample for
any realistic contention on this file given the codebase's write frequency.

### Read path

```csharp
private readonly record struct LoadResult(FeedMetadata? Metadata, string? Etag);

public async Task<FeedMetadata?> Load(string assetDir, CancellationToken ct = default)
{
    var result = await LoadWithEtag(FeedsJsonPath(assetDir), ct);
    return result.Metadata;
}

private async Task<LoadResult> LoadWithEtag(string path, CancellationToken ct)
{
    var stored = await _fs.ReadWithEtag(path, ct);
    if (stored is null) return new LoadResult(null, null);

    var node = JsonNode.Parse(stored.Content);
    FeedMetadataValidator.ValidateOrThrow(node);
    var metadata = JsonSerializer.Deserialize<FeedMetadata>(stored.Content, JsonOptions);
    return new LoadResult(metadata, stored.Etag);
}
```

No `_locks`. No `GetLock`. No `_fs.Exists` pre-check (folded into `ReadWithEtag`'s
`null` return).

### Retry helper (one place; called from every write method)

```csharp
/// <summary>
/// Reads, applies <paramref name="mutator"/>, conditionally writes. Retries on
/// concurrency conflict with jittered backoff up to <see cref="MaxAttempts"/>.
/// Mutator returning <c>null</c> is a no-op — no write happens; helper returns
/// <c>false</c>. On successful write returns <c>true</c>.
/// </summary>
private async Task<bool> UpdateWithRetry(
    string path,
    Func<FeedMetadata, FeedMetadata?> mutator,
    CancellationToken ct)
{
    for (var attempt = 0; ; attempt++)
    {
        var current = await LoadWithEtag(path, ct);
        var existing = current.Metadata ?? new FeedMetadata();
        var updated = mutator(existing);
        if (updated is null) return false;

        var json = SerializeAndValidate(updated);
        try
        {
            await _fs.WriteIfMatch(path, json, current.Etag, ct);
            return true;
        }
        catch (ConcurrencyConflictException) when (attempt < MaxAttempts - 1)
        {
            await Task.Delay(Backoff(attempt), ct);
        }
    }
}

private static string SerializeAndValidate(FeedMetadata metadata)
{
    var json = JsonSerializer.Serialize(metadata, JsonOptions);
    var node = JsonNode.Parse(json);
    FeedMetadataValidator.ValidateOrThrow(node);
    return json;
}
```

### Public write methods

Each shrinks to "compute the next `FeedMetadata`, hand it to `UpdateWithRetry`,
fire the event on success." Example (`EnsureSchema`):

```csharp
public async Task EnsureSchema(
    string assetDir, string feedName, string interval,
    string[] columns, AutoApplySpec? autoApply = null, CancellationToken ct = default)
{
    var path = FeedsJsonPath(assetDir);
    var autoApplyDef = autoApply is null ? null : new AutoApplyDefinition { /* … */ };

    await UpdateWithRetry(path, existing => new FeedMetadata
    {
        Feeds = new Dictionary<string, FeedDefinition>(existing.Feeds)
        {
            [feedName] = new FeedDefinition
            {
                Interval = interval, Columns = columns, AutoApply = autoApplyDef,
            }
        },
        Candles = existing.Candles,
    }, ct);

    ManifestChanged?.Invoke(Path.GetFullPath(assetDir));
}
```

Conditional case (`SetAutoApplyParams`):

```csharp
public async Task<bool> SetAutoApplyParams(
    string assetDir, string feedName,
    double? cap, double? floor, int? intervalHours, bool? disclaimer,
    CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(feedName);

    var path = FeedsJsonPath(assetDir);
    var written = await UpdateWithRetry(path, existing =>
    {
        if (!existing.Feeds.TryGetValue(feedName, out var feed) || feed.AutoApply is null)
            return null; // no-op signal

        return new FeedMetadata
        {
            Feeds = new Dictionary<string, FeedDefinition>(existing.Feeds)
            {
                [feedName] = feed with
                {
                    AutoApply = feed.AutoApply with
                    {
                        Cap = cap, Floor = floor,
                        IntervalHours = intervalHours, Disclaimer = disclaimer,
                    },
                },
            },
            Candles = existing.Candles,
        };
    }, ct);

    if (written) ManifestChanged?.Invoke(Path.GetFullPath(assetDir));
    return written;
}
```

`RemoveFeedsInternal` collapses similarly — the `raised` flag goes away,
replaced by `UpdateWithRetry`'s return value.

## Error handling

| Exception | Caught? | Action |
|---|---|---|
| `ConcurrencyConflictException` (write) | yes, inside retry | sleep `Backoff(attempt)`, retry; on `MaxAttempts - 1` it propagates |
| `OperationCanceledException` | no | propagates naturally to caller |
| `IOException` / `UnauthorizedAccessException` | no | propagates; not concurrency-related, not in scope to retry |
| `JsonException` / validator throws | no | propagates; this is a data-format error, not a concurrency error |

The retry helper deliberately catches *only* `ConcurrencyConflictException`. Other
faults must not be silently retried — they indicate a bug or environmental fault
that deserves to surface at the original call site.

## Net diff

| File | Change |
|---|---|
| `AlgoTradeForge.Storage.Abstractions/IO/IFileStorage.cs` | +2 methods (`ReadWithEtag`, `WriteIfMatch`) |
| `AlgoTradeForge.Storage.Abstractions/IO/StoredObject.cs` | new file |
| `AlgoTradeForge.Storage.Abstractions/IO/ConcurrencyConflictException.cs` | new file |
| `AlgoTradeForge.Storage/LocalFileStorage.cs` | +2 method impls; `FileShare.Delete` added to existing readers; `+_writeLocks` field (internal) |
| `AlgoTradeForge.HistoryLoader.Infrastructure/Storage/FeedSchemaManager.cs` | `−_locks`, `−GetLock`, `−LoadUnsafe`, `−AtomicWriteUnsafe`. `+LoadResult`, `+LoadWithEtag`, `+UpdateWithRetry`, `+SerializeAndValidate`. Each `Ensure*` / `Set*` / `Remove*` collapses to a mutator lambda |
| `AlgoTradeForge.HistoryLoader.Application.Catalog/FeedCatalog.cs` | none (`Load` signature unchanged) |
| `AlgoTradeForge.HistoryLoader.WebApi/AppSettingsWriter.cs` | none (out of scope) |
| tests | `+LocalFileStorageEtagTests`, `+FeedSchemaManagerConcurrencyTests`; existing `FeedSchemaManagerTests` get small touch-ups where they relied on internal locking |

## Testing

### LocalFileStorageEtagTests (new file)

Per-test temp directory fixture. Hits real file system. Tests:

| Test | Asserts |
|---|---|
| `ReadWithEtag_returns_null_when_key_absent` | Returns `null` for absent key |
| `ReadWithEtag_returns_content_and_etag_for_present_key` | Returns `(content, etag)` matching disk |
| `ReadWithEtag_etag_is_stable_for_unchanged_content` | Two sequential reads of same file → identical ETags |
| `ReadWithEtag_etag_differs_for_different_content` | Out-of-band overwrite → new ETag differs |
| `WriteIfMatch_creates_file_when_expected_null_and_file_absent` | First-write returns new ETag |
| `WriteIfMatch_throws_when_expected_null_but_file_present` | Create-only race throws `ConcurrencyConflictException` with `ExpectedEtag = null`, `ActualEtag = <current>` |
| `WriteIfMatch_succeeds_when_etag_matches` | Content replaced atomically, new ETag returned |
| `WriteIfMatch_throws_when_etag_stale` | Stale-ETag write → conflict; old content untouched |
| `WriteIfMatch_creates_parent_directory` | Writes to nested path create directory chain |
| `WriteIfMatch_does_not_leave_tmp_on_conflict` | After conflict throw, no `.tmp` orphan |
| `Reader_handle_does_not_block_concurrent_writer_rename` | Hold open `ReadWithEtag` stream; concurrent `WriteIfMatch` succeeds; reader finishes on pre-rename bytes. Regression guard for the Windows `FileShare.Delete` footgun |

### FeedSchemaManagerConcurrencyTests (new file)

Three tests against real `LocalFileStorage` over a temp dir (no mocks — the
contract is the whole point):

```csharp
[Fact]
public async Task Parallel_EnsureSchema_preserves_all_entries()
{
    const int N = 32;
    await Task.WhenAll(Enumerable.Range(0, N).Select(i =>
        _sut.EnsureSchema(assetDir, $"feed-{i}", "1m", ["ts", "value"], ct: default)));

    var manifest = await _sut.Load(assetDir);
    Assert.Equal(N, manifest!.Feeds.Count);
    for (var i = 0; i < N; i++)
        Assert.True(manifest.Feeds.ContainsKey($"feed-{i}"));
}

[Fact]
public async Task UpdateWithRetry_exhausts_attempts_and_throws_on_persistent_conflict()
{
    var fakeStorage = new AlwaysConflictingStorage();
    var sut = new FeedSchemaManager(fakeStorage);
    await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
        sut.EnsureSchema(assetDir, "feed-x", "1m", ["ts"], ct: default));
    Assert.Equal(MaxAttempts, fakeStorage.WriteAttempts);
}

[Fact]
public async Task UpdateWithRetry_honors_cancellation_between_attempts()
{
    using var cts = new CancellationTokenSource();
    var fakeStorage = new ConflictThenCancelStorage(cts);
    var sut = new FeedSchemaManager(fakeStorage);
    await Assert.ThrowsAsync<OperationCanceledException>(() =>
        sut.EnsureSchema(assetDir, "feed-x", "1m", ["ts"], ct: cts.Token));
}
```

The first test is the design's existence proof. It runs against the real file
system with no mocking. If it passes, the whole design works as advertised. If
any feed-id is missing in the final manifest, there is a race the retry loop is
not catching.

### Existing tests — touch-ups

Audit of `FeedSchemaManagerTests` and adjacent tests:

- Tests that call `Load` / `EnsureSchema` / etc. without concurrency assumptions:
  no change needed. Public observable behavior is identical.
- Tests that mocked `IFileStorage` and only expected `ReadAllText` / `WriteAllText`
  calls: substitute `ReadWithEtag` / `WriteIfMatch`. A handful of substitutions.
- `FeedCatalogTests` (if present): unchanged. `Load` signature preserved.

### Manual smoke check

After unit/integration tests are green:

1. Run WebApi + HistoryLoader against a populated `BTCUSDT_perp` data root.
2. In the Data tab, rapid-click "+" on `EqV_1m_5M` (the original repro).
3. Verify: no debugger break on `OperationCanceledException` /
   `TaskCanceledException` from any gate path. Feed status loads. Aggregation
   options load.

## Out of scope (deliberately)

* `AppSettingsWriter` migration to the same primitive. Same shape, low contention,
  no urgency. Worth doing as a follow-up.
* Cross-process `LocalFileStorage` safety. The HistoryLoader is single-process.
  The internal per-key `SemaphoreSlim` in `LocalFileStorage` makes the same
  single-process assumption the current code makes. If multi-process local FS
  ever becomes a requirement, an OS-level lock file (or migration to S3) is the
  correct answer — not stretching the in-memory semaphore.
* `FeedCatalog.GetAsset` cache reuse via `GetAssetsByExchange`. Orthogonal
  efficiency win; not a fix for this bug.
* Removal of `WriteLockManager`, `RunProgressCache._keyLocks`,
  `BinanceLiveAccountManager._accountLocks`. Each guards a different invariant
  unrelated to blob CAS.

## Open questions

None outstanding at design time. Implementation may surface edge cases (e.g., how
`File.ReadAllBytesAsync` interacts with the writer-side lock under exotic disk
errors); they get resolved in the implementation plan.
