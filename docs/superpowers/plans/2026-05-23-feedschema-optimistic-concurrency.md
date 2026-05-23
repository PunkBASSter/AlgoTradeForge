# FeedSchemaManager optimistic concurrency — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the per-`feeds.json`-path `SemaphoreSlim` from `FeedSchemaManager` so concurrent reads stop blocking each other (and stop tripping `OperationCanceledException` when clients abort while queued). Move writer-writer coordination down to `IFileStorage` via two new primitives — `ReadWithEtag` and `WriteIfMatch` — with bounded jittered-backoff retry in the manager.

**Architecture:** `FeedSchemaManager` keeps its public API; internals collapse to `LoadWithEtag` + a single `UpdateWithRetry` helper that every write method funnels through. Reads become lock-free and cancellable only at the actual I/O. The storage layer owns the conditional-write atomicity primitive. `LocalFileStorage` derives an opaque ETag as SHA-1 hex of bytes and holds an internal per-key writer-only mutex during the brief check-and-rename; readers add `FileShare.Delete` to their `FileStream` so writer renames don't collide with open read handles. `S3FileStorage` maps `ReadWithEtag` to `GetObject` (native `ETag` header) and `WriteIfMatch` to `PutObject` with the `If-Match` / `If-None-Match` headers (AWSSDK.S3 v4.0.23.3 supports them natively).

**Tech Stack:** C# 14 / .NET 10, xUnit, NSubstitute. No new NuGet packages. AWSSDK.S3 already at a sufficient version for conditional puts.

**Spec:** [`docs/superpowers/specs/2026-05-23-feedschema-optimistic-concurrency-design.md`](../specs/2026-05-23-feedschema-optimistic-concurrency-design.md)

**Branch:** `032-decouple-from-local-file-system` (active)

**Process notes (from CLAUDE.md):**
- **Only one `dotnet` process at a time.** Never run build/test in parallel. Wait for each command to finish.
- **Shell:** Windows PowerShell 5.1 (`powershell.exe`) — `pwsh` is unavailable. The `Bash` tool is also available for POSIX scripts.
- **Comment convention:** prefer no XML/inline comments; only when the *why* is non-obvious. One short line max if you do.

---

## File map

**Create:**
- `src/AlgoTradeForge.Storage.Abstractions/IO/StoredObject.cs`
- `src/AlgoTradeForge.Storage.Abstractions/IO/ConcurrencyConflictException.cs`
- `tests/AlgoTradeForge.HistoryLoader.Tests/Storage/FeedSchemaManagerOptimisticConcurrencyTests.cs`

**Modify:**
- `src/AlgoTradeForge.Storage.Abstractions/IO/IFileStorage.cs` — add 2 methods.
- `src/AlgoTradeForge.Storage/LocalFileStorage.cs` — add 2 method impls; add `FileShare.Delete` to existing readers; add internal per-key write lock.
- `src/AlgoTradeForge.Storage/S3FileStorage.cs` — add 2 method impls.
- `src/AlgoTradeForge.HistoryLoader.Infrastructure/Storage/FeedSchemaManager.cs` — full rewrite (remove `_locks`, add `UpdateWithRetry`).
- `tests/AlgoTradeForge.Infrastructure.Tests/IO/FileStorageContractTests.cs` — add ETag/Conditional contract tests.

**Untouched but exercised by regression:**
- `tests/AlgoTradeForge.HistoryLoader.Tests/Storage/FeedSchemaManagerStressTests.cs` — already has the canary "two parallel writers, both feeds persist" test. Must keep passing.
- `tests/AlgoTradeForge.HistoryLoader.Tests/Storage/FeedSchemaManagerTests.cs` — unit tests for the existing methods. Must keep passing.

---

## Task 1 — New types: `StoredObject` and `ConcurrencyConflictException`

**Files:**
- Create: `src/AlgoTradeForge.Storage.Abstractions/IO/StoredObject.cs`
- Create: `src/AlgoTradeForge.Storage.Abstractions/IO/ConcurrencyConflictException.cs`

These are pure data types. No standalone tests — they get exercised through the `IFileStorage` contract tests in later tasks.

- [ ] **Step 1: Create `StoredObject.cs`**

```csharp
namespace AlgoTradeForge.Storage;

public sealed record StoredObject(string Content, string Etag);
```

- [ ] **Step 2: Create `ConcurrencyConflictException.cs`**

```csharp
namespace AlgoTradeForge.Storage;

public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(string key, string? expectedEtag, string? actualEtag)
        : base($"Concurrency conflict on '{key}': expected etag '{expectedEtag ?? "<absent>"}', actual '{actualEtag ?? "<absent>"}'.")
    {
        Key = key;
        ExpectedEtag = expectedEtag;
        ActualEtag = actualEtag;
    }

    public string Key { get; }
    public string? ExpectedEtag { get; }
    public string? ActualEtag { get; }
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/AlgoTradeForge.Storage.Abstractions/AlgoTradeForge.Storage.Abstractions.csproj`
Expected: succeeds with 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/AlgoTradeForge.Storage.Abstractions/IO/StoredObject.cs src/AlgoTradeForge.Storage.Abstractions/IO/ConcurrencyConflictException.cs
git commit -m "feat(storage): add StoredObject + ConcurrencyConflictException types"
```

---

## Task 2 — Extend `IFileStorage` interface with stub impls

The interface change forces both backends to declare the methods. We stub-throw in both so the rest of the codebase still builds; real impls land in tasks 3, 4, and 6.

**Files:**
- Modify: `src/AlgoTradeForge.Storage.Abstractions/IO/IFileStorage.cs`
- Modify: `src/AlgoTradeForge.Storage/LocalFileStorage.cs`
- Modify: `src/AlgoTradeForge.Storage/S3FileStorage.cs`

- [ ] **Step 1: Append to `IFileStorage`**

After the existing `WriteAllBytes` declaration in `IFileStorage.cs`, add:

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

- [ ] **Step 2: Stub in `LocalFileStorage`** (append before the closing brace of `LocalFileStorage` class, above the nested `LocalWriteSession`):

```csharp
public Task<StoredObject?> ReadWithEtag(string key, CancellationToken ct = default)
    => throw new NotImplementedException("Task 3 implements this.");

public Task<string> WriteIfMatch(string key, string content, string? expectedEtag, CancellationToken ct = default)
    => throw new NotImplementedException("Task 4 implements this.");
```

- [ ] **Step 3: Stub in `S3FileStorage`** (append at the end of the class, before the closing brace):

```csharp
public Task<StoredObject?> ReadWithEtag(string key, CancellationToken ct = default)
    => throw new NotImplementedException("Task 6 implements this.");

public Task<string> WriteIfMatch(string key, string content, string? expectedEtag, CancellationToken ct = default)
    => throw new NotImplementedException("Task 6 implements this.");
```

- [ ] **Step 4: Build the full solution to verify nothing else broke**

Run: `dotnet build AlgoTradeForge.slnx`
Expected: succeeds with 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.Storage.Abstractions/IO/IFileStorage.cs src/AlgoTradeForge.Storage/LocalFileStorage.cs src/AlgoTradeForge.Storage/S3FileStorage.cs
git commit -m "feat(storage): declare ReadWithEtag + WriteIfMatch on IFileStorage (stubbed)"
```

---

## Task 3 — TDD: `LocalFileStorage.ReadWithEtag`

The contract tests live in the shared base `FileStorageContractTests`, so they automatically run against `LocalFileStorageContractTests` and `S3FileStorageContractTests`. We'll get `LocalFileStorage` green here; `S3FileStorage` stays red until Task 6.

**Files:**
- Modify: `tests/AlgoTradeForge.Infrastructure.Tests/IO/FileStorageContractTests.cs`
- Modify: `src/AlgoTradeForge.Storage/LocalFileStorage.cs`

- [ ] **Step 1: Add failing tests to `FileStorageContractTests`**

Append these inside the `FileStorageContractTests` class (above the closing brace):

```csharp
[Fact]
public async Task ReadWithEtag_ReturnsNull_WhenKeyAbsent()
{
    var result = await Storage.ReadWithEtag(Key("absent.json"), Ct);
    Assert.Null(result);
}

[Fact]
public async Task ReadWithEtag_ReturnsContentAndEtag_WhenPresent()
{
    var key = Key("etag/present.json");
    await Storage.WriteAllText(key, "{\"x\":1}", Encoding.UTF8, Ct);

    var result = await Storage.ReadWithEtag(key, Ct);

    Assert.NotNull(result);
    Assert.Equal("{\"x\":1}", result!.Content);
    Assert.False(string.IsNullOrEmpty(result.Etag));
}

[Fact]
public async Task ReadWithEtag_EtagIsStable_ForUnchangedContent()
{
    var key = Key("etag/stable.json");
    await Storage.WriteAllText(key, "stable-content", Encoding.UTF8, Ct);

    var a = await Storage.ReadWithEtag(key, Ct);
    var b = await Storage.ReadWithEtag(key, Ct);

    Assert.Equal(a!.Etag, b!.Etag);
}

[Fact]
public async Task ReadWithEtag_EtagDiffers_AfterContentChange()
{
    var key = Key("etag/changes.json");
    await Storage.WriteAllText(key, "first", Encoding.UTF8, Ct);
    var first = await Storage.ReadWithEtag(key, Ct);

    await Storage.WriteAllText(key, "second", Encoding.UTF8, Ct);
    var second = await Storage.ReadWithEtag(key, Ct);

    Assert.NotEqual(first!.Etag, second!.Etag);
}
```

- [ ] **Step 2: Run the failing tests against LocalFileStorage**

Run: `dotnet test tests/AlgoTradeForge.Infrastructure.Tests/AlgoTradeForge.Infrastructure.Tests.csproj --filter "FullyQualifiedName~LocalFileStorageContractTests&FullyQualifiedName~ReadWithEtag"`
Expected: 4 tests fail with `NotImplementedException`.

- [ ] **Step 3: Implement `ReadWithEtag` in `LocalFileStorage`**

Add (or replace the stub of) `ReadWithEtag`. Also add the `EtagOf` helpers (private static, used by both `ReadWithEtag` and `WriteIfMatch` later):

```csharp
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

private static string EtagOf(string content) => EtagOf(Encoding.UTF8.GetBytes(content));

private static string EtagOf(ReadOnlySpan<byte> bytes)
{
    Span<byte> hash = stackalloc byte[20];
    System.Security.Cryptography.SHA1.HashData(bytes, hash);
    return Convert.ToHexString(hash);
}
```

(`SHA1` is in `System.Security.Cryptography`. If a `using` for that namespace isn't already at the top of `LocalFileStorage.cs`, add `using System.Security.Cryptography;`.)

- [ ] **Step 4: Run the tests again**

Run: `dotnet test tests/AlgoTradeForge.Infrastructure.Tests/AlgoTradeForge.Infrastructure.Tests.csproj --filter "FullyQualifiedName~LocalFileStorageContractTests&FullyQualifiedName~ReadWithEtag"`
Expected: all 4 tests pass.

- [ ] **Step 5: Commit**

```bash
git add tests/AlgoTradeForge.Infrastructure.Tests/IO/FileStorageContractTests.cs src/AlgoTradeForge.Storage/LocalFileStorage.cs
git commit -m "feat(storage): implement LocalFileStorage.ReadWithEtag (SHA-1 bytes)"
```

---

## Task 4 — TDD: `LocalFileStorage.WriteIfMatch`

**Files:**
- Modify: `tests/AlgoTradeForge.Infrastructure.Tests/IO/FileStorageContractTests.cs`
- Modify: `src/AlgoTradeForge.Storage/LocalFileStorage.cs`

- [ ] **Step 1: Add failing tests to `FileStorageContractTests`** (append in the same class):

```csharp
[Fact]
public async Task WriteIfMatch_CreatesFile_WhenExpectedNullAndAbsent()
{
    var key = Key("cas/create.json");
    var etag = await Storage.WriteIfMatch(key, "fresh", expectedEtag: null, Ct);

    Assert.False(string.IsNullOrEmpty(etag));
    Assert.Equal("fresh", await Storage.ReadAllText(key, Ct));
}

[Fact]
public async Task WriteIfMatch_Throws_WhenExpectedNullButPresent()
{
    var key = Key("cas/already-exists.json");
    await Storage.WriteAllText(key, "existing", Encoding.UTF8, Ct);

    var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
        Storage.WriteIfMatch(key, "new", expectedEtag: null, Ct));

    Assert.Equal(key, ex.Key);
    Assert.Null(ex.ExpectedEtag);
    Assert.False(string.IsNullOrEmpty(ex.ActualEtag));
    Assert.Equal("existing", await Storage.ReadAllText(key, Ct));
}

[Fact]
public async Task WriteIfMatch_Succeeds_WhenEtagMatches()
{
    var key = Key("cas/match.json");
    await Storage.WriteAllText(key, "v1", Encoding.UTF8, Ct);
    var current = await Storage.ReadWithEtag(key, Ct);

    var newEtag = await Storage.WriteIfMatch(key, "v2", current!.Etag, Ct);

    Assert.NotEqual(current.Etag, newEtag);
    Assert.Equal("v2", await Storage.ReadAllText(key, Ct));
}

[Fact]
public async Task WriteIfMatch_Throws_WhenEtagStale()
{
    var key = Key("cas/stale.json");
    await Storage.WriteAllText(key, "v1", Encoding.UTF8, Ct);
    var stale = await Storage.ReadWithEtag(key, Ct);
    await Storage.WriteAllText(key, "v2", Encoding.UTF8, Ct);

    var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
        Storage.WriteIfMatch(key, "v3", stale!.Etag, Ct));

    Assert.Equal(stale.Etag, ex.ExpectedEtag);
    Assert.NotEqual(stale.Etag, ex.ActualEtag);
    Assert.Equal("v2", await Storage.ReadAllText(key, Ct));
}

[Fact]
public async Task WriteIfMatch_CreatesParentDirectory()
{
    var key = Key("cas/nested/deeper/leaf.json");

    await Storage.WriteIfMatch(key, "hi", expectedEtag: null, Ct);

    Assert.Equal("hi", await Storage.ReadAllText(key, Ct));
}
```

- [ ] **Step 2: Run the failing tests**

Run: `dotnet test tests/AlgoTradeForge.Infrastructure.Tests/AlgoTradeForge.Infrastructure.Tests.csproj --filter "FullyQualifiedName~LocalFileStorageContractTests&FullyQualifiedName~WriteIfMatch"`
Expected: 5 tests fail with `NotImplementedException`.

- [ ] **Step 3: Add the internal per-key write lock field to `LocalFileStorage`**

Near the top of the class, alongside `_dataRoot`:

```csharp
private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new();

private SemaphoreSlim WriteLock(string fullPath) =>
    _writeLocks.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
```

If `using System.Collections.Concurrent;` is not already at the top of the file, add it and drop the `System.Collections.Concurrent.` qualifier from the field declaration.

- [ ] **Step 4: Implement `WriteIfMatch`** (replace the stub):

```csharp
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
```

Note: `SemaphoreSlimExtensions.LockAsync` is the existing project helper (the same one `FeedSchemaManager` uses today). `using AlgoTradeForge.Storage.Threading;` should already be a global using or available in this file; if not, add the using.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/AlgoTradeForge.Infrastructure.Tests/AlgoTradeForge.Infrastructure.Tests.csproj --filter "FullyQualifiedName~LocalFileStorageContractTests&FullyQualifiedName~WriteIfMatch"`
Expected: all 5 tests pass.

- [ ] **Step 6: Commit**

```bash
git add tests/AlgoTradeForge.Infrastructure.Tests/IO/FileStorageContractTests.cs src/AlgoTradeForge.Storage/LocalFileStorage.cs
git commit -m "feat(storage): implement LocalFileStorage.WriteIfMatch (SHA-1 CAS, per-key write lock)"
```

---

## Task 5 — Add `FileShare.Delete` to readers + regression test

Without `FileShare.Delete` on the reader handle, a writer's `File.Move(overwrite: true)` fails on Windows with `IOException: The process cannot access the file because it is being used by another process` when a reader is mid-read. This is the original footgun the old `SemaphoreSlim`-on-reads was masking. We add a focused regression test, then update reader open flags.

**Files:**
- Modify: `tests/AlgoTradeForge.Infrastructure.Tests/IO/FileStorageContractTests.cs`
- Modify: `src/AlgoTradeForge.Storage/LocalFileStorage.cs`

- [ ] **Step 1: Add the failing regression test to `FileStorageContractTests`** (append in the same class):

```csharp
[Fact]
public async Task Reader_DoesNotBlock_ConcurrentWriterRename()
{
    var key = Key("share-delete/race.json");
    await Storage.WriteAllText(key, "v1", Encoding.UTF8, Ct);

    // Hold an open read stream (simulating a reader that hasn't finished consuming).
    await using var openRead = await Storage.OpenRead(key, Ct);

    // While the read stream is open, a writer must be able to atomically replace
    // the file. Without FileShare.Delete on the reader, this throws IOException
    // on Windows because MoveFileEx(REPLACE_EXISTING) cannot delete a file that
    // another handle holds open without the delete-share permission.
    await Storage.WriteAllText(key, "v2", Encoding.UTF8, Ct);

    Assert.Equal("v2", await Storage.ReadAllText(key, Ct));
}
```

- [ ] **Step 2: Run the failing test**

Run: `dotnet test tests/AlgoTradeForge.Infrastructure.Tests/AlgoTradeForge.Infrastructure.Tests.csproj --filter "FullyQualifiedName~LocalFileStorageContractTests&FullyQualifiedName~Reader_DoesNotBlock"`
Expected on Windows: test fails with `IOException` thrown from `File.Move`.

(If the test happens to pass on a non-Windows host, that's because POSIX rename semantics let `unlink`+`rename` succeed with open handles. The fix still matters for Windows production.)

- [ ] **Step 3: Update reader paths in `LocalFileStorage`**

Find and update the three reader sites in `LocalFileStorage.cs`. In each, change `FileShare.ReadWrite` to `FileShare.ReadWrite | FileShare.Delete`:

- `OpenRead`: line ~89.
- `ReadAllText`: line ~95.
- `ReadLines`: line ~110.

The pattern to replace is:
```csharp
new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, DefaultBufferSize, useAsync: true)
```
becomes:
```csharp
new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, DefaultBufferSize, useAsync: true)
```

The `ReadWithEtag` impl from Task 3 already uses `FileShare.ReadWrite | FileShare.Delete` — no change there.

- [ ] **Step 4: Re-run the regression test plus the full contract suite**

Run: `dotnet test tests/AlgoTradeForge.Infrastructure.Tests/AlgoTradeForge.Infrastructure.Tests.csproj --filter "FullyQualifiedName~LocalFileStorageContractTests"`
Expected: all tests pass (including the new regression test).

- [ ] **Step 5: Commit**

```bash
git add tests/AlgoTradeForge.Infrastructure.Tests/IO/FileStorageContractTests.cs src/AlgoTradeForge.Storage/LocalFileStorage.cs
git commit -m "fix(storage): add FileShare.Delete to readers so writer renames don't collide"
```

---

## Task 6 — Implement `S3FileStorage.ReadWithEtag` and `WriteIfMatch`

The same shared `FileStorageContractTests` already runs against `S3FileStorageContractTests` (when configured with valid S3 creds). The tests added in Tasks 3 + 4 + 5 are currently failing for S3 because the impls are stubs. This task makes them pass.

**Files:**
- Modify: `src/AlgoTradeForge.Storage/S3FileStorage.cs`

S3 specifics:
- `ReadWithEtag` → `GetObject`; the response `ETag` header is the native ETag (MD5 hex for non-multipart objects, surrounded by double-quotes — strip them for a consistent string).
- `WriteIfMatch` with `expectedEtag == null` → `PutObject` with `IfNoneMatch = "*"` (S3's "fail if object exists").
- `WriteIfMatch` with `expectedEtag != null` → `PutObject` with `IfMatch = "\"" + expectedEtag + "\""` (re-wrap in quotes for S3's wire format).
- Map S3's `412 PreconditionFailed` → `ConcurrencyConflictException`. On a `412` from `IfNoneMatch=*`, `expectedEtag` is `null` and `actualEtag` is unknown without a separate HEAD; in that case we issue a single `HeadObject` to populate `ActualEtag` for the exception (best-effort — if it 404s in the meantime, surface as `null`).

- [ ] **Step 1: Implement `ReadWithEtag` in `S3FileStorage`** (replace the stub):

```csharp
public async Task<StoredObject?> ReadWithEtag(string key, CancellationToken ct = default)
{
    try
    {
        using var resp = await _client.GetObjectAsync(_bucket, ToS3Key(key), ct);
        using var reader = new StreamReader(resp.ResponseStream);
        var content = await reader.ReadToEndAsync(ct);
        return new StoredObject(content, StripQuotes(resp.ETag));
    }
    catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
    {
        return null;
    }
}

private static string StripQuotes(string etag) =>
    string.IsNullOrEmpty(etag) ? etag :
    etag.Length >= 2 && etag[0] == '"' && etag[^1] == '"' ? etag[1..^1] : etag;
```

- [ ] **Step 2: Implement `WriteIfMatch` in `S3FileStorage`** (replace the stub):

```csharp
public async Task<string> WriteIfMatch(string key, string content, string? expectedEtag, CancellationToken ct = default)
{
    var s3Key = ToS3Key(key);
    var request = new PutObjectRequest
    {
        BucketName = _bucket,
        Key = s3Key,
        ContentBody = content,
        ContentType = "application/octet-stream",
    };

    if (expectedEtag is null)
        request.IfNoneMatch = "*";
    else
        request.IfMatch = $"\"{expectedEtag}\"";

    try
    {
        var resp = await _client.PutObjectAsync(request, ct);
        return StripQuotes(resp.ETag);
    }
    catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
    {
        var actual = await TryGetCurrentEtag(s3Key, ct);
        throw new ConcurrencyConflictException(key, expectedEtag, actual);
    }
}

private async Task<string?> TryGetCurrentEtag(string s3Key, CancellationToken ct)
{
    try
    {
        var meta = await _client.GetObjectMetadataAsync(_bucket, s3Key, ct);
        return StripQuotes(meta.ETag);
    }
    catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
    {
        return null;
    }
}
```

- [ ] **Step 3: Run the S3 contract suite**

If the local S3 contract tests are configured (env vars for endpoint + creds, or a MinIO/LocalStack mock), run:

Run: `dotnet test tests/AlgoTradeForge.Infrastructure.Tests/AlgoTradeForge.Infrastructure.Tests.csproj --filter "FullyQualifiedName~S3FileStorageContractTests"`
Expected: all tests pass — including the four `ReadWithEtag_*`, five `WriteIfMatch_*`, and the `Reader_DoesNotBlock_ConcurrentWriterRename` tests added earlier.

If S3 isn't available in this environment, this task's verification deferred to CI / smoke. **The Local suite must still pass** — confirm:
Run: `dotnet test tests/AlgoTradeForge.Infrastructure.Tests/AlgoTradeForge.Infrastructure.Tests.csproj --filter "FullyQualifiedName~LocalFileStorageContractTests"`
Expected: all tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/AlgoTradeForge.Storage/S3FileStorage.cs
git commit -m "feat(storage): implement S3FileStorage.ReadWithEtag + WriteIfMatch (native If-Match)"
```

---

## Task 7 — Rewrite `FeedSchemaManager`

This is the biggest task in lines-of-code, but mechanically simple: replace the per-path `SemaphoreSlim` machinery with a single `UpdateWithRetry` helper, and route every public write method through it. The existing tests (`FeedSchemaManagerTests`, `FeedSchemaManagerStressTests`) must keep passing without modification.

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Storage/FeedSchemaManager.cs` (full rewrite of internals)

- [ ] **Step 1: Run existing tests as a baseline** (must be green before rewrite)

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/AlgoTradeForge.HistoryLoader.Tests.csproj --filter "FullyQualifiedName~FeedSchemaManager"`
Expected: all tests pass on the current (pre-rewrite) code.

- [ ] **Step 2: Replace the `FeedSchemaManager` body**

Open `src/AlgoTradeForge.HistoryLoader.Infrastructure/Storage/FeedSchemaManager.cs`. Keep the using-statements at the top. Replace the entire body of the `FeedSchemaManager` class (everything between the class's opening `{` and closing `}`) with:

```csharp
private readonly IFileStorage _fs;

public event Action<string>? ManifestChanged;

private const int MaxAttempts = 5;

private static readonly JsonSerializerOptions JsonOptions = new()
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

public FeedSchemaManager(IFileStorage fs)
{
    _fs = fs;
}

public async Task<FeedMetadata?> Load(string assetDir, CancellationToken ct = default)
{
    var result = await LoadWithEtag(FeedsJsonPath(assetDir), ct);
    return result.Metadata;
}

public async Task EnsureSchema(
    string assetDir,
    string feedName,
    string interval,
    string[] columns,
    AutoApplySpec? autoApply = null,
    CancellationToken ct = default)
{
    var path = FeedsJsonPath(assetDir);
    AutoApplyDefinition? autoApplyDef = autoApply is null ? null : new AutoApplyDefinition
    {
        Type = autoApply.Type,
        RateColumn = autoApply.RateColumn,
        SignConvention = autoApply.SignConvention,
        Cap = autoApply.Cap,
        Floor = autoApply.Floor,
        IntervalHours = autoApply.IntervalHours,
        Disclaimer = autoApply.Disclaimer,
    };

    await UpdateWithRetry(path, existing => new FeedMetadata
    {
        Feeds = new Dictionary<string, FeedDefinition>(existing.Feeds)
        {
            [feedName] = new FeedDefinition
            {
                Interval = interval,
                Columns = columns,
                AutoApply = autoApplyDef,
            }
        },
        Candles = existing.Candles,
    }, ct);

    ManifestChanged?.Invoke(Path.GetFullPath(assetDir));
}

public async Task EnsureAltBarFeed(string assetDir, string feedId, AltBarFeedSpec spec, CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(feedId);
    ArgumentNullException.ThrowIfNull(spec);

    var path = FeedsJsonPath(assetDir);

    await UpdateWithRetry(path, existing => new FeedMetadata
    {
        Feeds = new Dictionary<string, FeedDefinition>(existing.Feeds)
        {
            [feedId] = new FeedDefinition
            {
                Kind = spec.Kind,
                Columns = spec.Columns,
                Type = spec.Type,
                Source = spec.Source,
                Threshold = spec.Threshold,
                Build = spec.Build,
                Fidelity = spec.Fidelity,
                FirstBarTs = spec.FirstBarTs,
                LastBarTs = spec.LastBarTs,
                Sidecar = spec.Sidecar,
            }
        },
        Candles = existing.Candles,
    }, ct);

    ManifestChanged?.Invoke(Path.GetFullPath(assetDir));
}

public async Task EnsureAltBarWithSidecar(
    string assetDir,
    string parentFeedId,
    AltBarFeedSpec parentSpec,
    string sidecarFeedId,
    string[] sidecarColumns,
    CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(parentFeedId);
    ArgumentException.ThrowIfNullOrEmpty(sidecarFeedId);
    ArgumentNullException.ThrowIfNull(parentSpec);
    ArgumentNullException.ThrowIfNull(sidecarColumns);

    var path = FeedsJsonPath(assetDir);

    await UpdateWithRetry(path, existing =>
    {
        var parentEntry = new FeedDefinition
        {
            Kind = parentSpec.Kind,
            Columns = parentSpec.Columns,
            Type = parentSpec.Type,
            Source = parentSpec.Source,
            Threshold = parentSpec.Threshold,
            Build = parentSpec.Build,
            Fidelity = parentSpec.Fidelity,
            FirstBarTs = parentSpec.FirstBarTs,
            LastBarTs = parentSpec.LastBarTs,
            Sidecar = sidecarFeedId,
        };

        var sidecarEntry = new FeedDefinition
        {
            Kind = "Side",
            Columns = sidecarColumns,
            NullableColumns = true,
        };

        return new FeedMetadata
        {
            Feeds = new Dictionary<string, FeedDefinition>(existing.Feeds)
            {
                [parentFeedId] = parentEntry,
                [sidecarFeedId] = sidecarEntry,
            },
            Candles = existing.Candles,
        };
    }, ct);

    ManifestChanged?.Invoke(Path.GetFullPath(assetDir));
}

public async Task<bool> SetAutoApplyParams(
    string assetDir,
    string feedName,
    double? cap,
    double? floor,
    int? intervalHours,
    bool? disclaimer,
    CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(feedName);

    var path = FeedsJsonPath(assetDir);
    var written = await UpdateWithRetry(path, existing =>
    {
        if (!existing.Feeds.TryGetValue(feedName, out var feed) || feed.AutoApply is null)
            return null;

        var updatedAutoApply = new AutoApplyDefinition
        {
            Type = feed.AutoApply.Type,
            RateColumn = feed.AutoApply.RateColumn,
            SignConvention = feed.AutoApply.SignConvention,
            Cap = cap,
            Floor = floor,
            IntervalHours = intervalHours,
            Disclaimer = disclaimer,
        };

        var updatedFeed = new FeedDefinition
        {
            Kind = feed.Kind,
            Interval = feed.Interval,
            Columns = feed.Columns,
            AutoApply = updatedAutoApply,
            Type = feed.Type,
            Source = feed.Source,
            Threshold = feed.Threshold,
            Build = feed.Build,
            Fidelity = feed.Fidelity,
            FirstBarTs = feed.FirstBarTs,
            LastBarTs = feed.LastBarTs,
            Sidecar = feed.Sidecar,
            NullableColumns = feed.NullableColumns,
        };

        return new FeedMetadata
        {
            Feeds = new Dictionary<string, FeedDefinition>(existing.Feeds) { [feedName] = updatedFeed },
            Candles = existing.Candles,
        };
    }, ct);

    if (written) ManifestChanged?.Invoke(Path.GetFullPath(assetDir));
    return written;
}

public Task RemoveFeed(string assetDir, string feedId, CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(feedId);
    return RemoveFeedsInternal(assetDir, [feedId], ct);
}

public Task RemoveFeedAndSidecar(string assetDir, string feedId, string sidecarFeedId, CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(feedId);
    ArgumentException.ThrowIfNullOrEmpty(sidecarFeedId);
    return RemoveFeedsInternal(assetDir, [feedId, sidecarFeedId], ct);
}

private async Task RemoveFeedsInternal(string assetDir, string[] feedIds, CancellationToken ct)
{
    var path = FeedsJsonPath(assetDir);
    var written = await UpdateWithRetry(path, existing =>
    {
        var updated = new Dictionary<string, FeedDefinition>(existing.Feeds);
        var removedAny = false;
        foreach (var id in feedIds)
            if (updated.Remove(id)) removedAny = true;
        if (!removedAny) return null;
        return new FeedMetadata { Feeds = updated, Candles = existing.Candles };
    }, ct);

    if (written) ManifestChanged?.Invoke(Path.GetFullPath(assetDir));
}

public async Task EnsureCandleConfig(string assetDir, int decimalDigits, string interval, CancellationToken ct = default)
{
    var path = FeedsJsonPath(assetDir);
    await UpdateWithRetry(path, existing =>
    {
        var scaleFactor = (decimal)Math.Pow(10, decimalDigits);
        var existingIntervals = existing.Candles?.Intervals ?? [];
        var updatedIntervals = existingIntervals.Contains(interval)
            ? existingIntervals
            : [..existingIntervals, interval];

        return new FeedMetadata
        {
            Feeds = existing.Feeds,
            Candles = new CandleConfig
            {
                ScaleFactor = scaleFactor,
                Intervals = updatedIntervals,
            },
        };
    }, ct);

    ManifestChanged?.Invoke(Path.GetFullPath(assetDir));
}

private static string FeedsJsonPath(string assetDir) =>
    Path.GetFullPath(Path.Combine(assetDir, "feeds.json"));

private readonly record struct LoadResult(FeedMetadata? Metadata, string? Etag);

private async Task<LoadResult> LoadWithEtag(string path, CancellationToken ct)
{
    var stored = await _fs.ReadWithEtag(path, ct);
    if (stored is null) return new LoadResult(null, null);

    var node = JsonNode.Parse(stored.Content);
    FeedMetadataValidator.ValidateOrThrow(node);
    var metadata = JsonSerializer.Deserialize<FeedMetadata>(stored.Content, JsonOptions);
    return new LoadResult(metadata, stored.Etag);
}

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

private static TimeSpan Backoff(int attempt) =>
    TimeSpan.FromMilliseconds(Random.Shared.Next(5, 21) * (1 << attempt));
```

Notes on what's removed:
- `private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();` — gone.
- `GetLock(...)` method — gone.
- `LoadUnsafe(...)` — folded into `LoadWithEtag`.
- `AtomicWriteUnsafe(...)` — folded into `UpdateWithRetry`.
- Every `using (var _ = await gate.LockAsync(ct))` block — gone.

Also: remove the `using AlgoTradeForge.Storage.Threading;` if it's no longer used (the only consumer was `gate.LockAsync`). Remove `using System.Collections.Concurrent;` if no longer used.

- [ ] **Step 3: Build the solution**

Run: `dotnet build AlgoTradeForge.slnx`
Expected: succeeds with 0 errors. (Warnings about unused usings should be addressed.)

- [ ] **Step 4: Run all `FeedSchemaManager`-related tests**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/AlgoTradeForge.HistoryLoader.Tests.csproj --filter "FullyQualifiedName~FeedSchemaManager"`
Expected: all tests pass — including `FeedSchemaManagerTests`, `FeedSchemaManagerCascadeTests`, and crucially `FeedSchemaManagerStressTests.ConcurrentWriters_DistinctFeedIds_AllEntriesPersist` (100 iterations of parallel writers — the canary).

The stress test passing is the design's existence proof: it proves the new optimistic-concurrency loop preserves the same "no entry overwritten" invariant the old `SemaphoreSlim` did.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Infrastructure/Storage/FeedSchemaManager.cs
git commit -m "refactor(history): replace FeedSchemaManager per-path lock with optimistic CAS via IFileStorage"
```

---

## Task 8 — Add retry-exhaustion + cancellation tests for `FeedSchemaManager`

The existing stress test proves the happy path under contention. We need two more focused tests to prove the failure paths:
1. When conflicts persist longer than `MaxAttempts`, the final `ConcurrencyConflictException` propagates (no infinite loop).
2. When the caller's `CancellationToken` fires between retry attempts, `OperationCanceledException` propagates promptly.

We use NSubstitute (already a project dependency per CLAUDE.md) to inject a controlled fake `IFileStorage`.

**Files:**
- Create: `tests/AlgoTradeForge.HistoryLoader.Tests/Storage/FeedSchemaManagerOptimisticConcurrencyTests.cs`

- [ ] **Step 1: Create the test file**

```csharp
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using AlgoTradeForge.Storage;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Storage;

public sealed class FeedSchemaManagerOptimisticConcurrencyTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"FeedSchemaManagerOcc_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string AssetDir(string name)
    {
        var path = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PersistentConflict_ExhaustsMaxAttempts_ThenThrows()
    {
        var fs = Substitute.For<IFileStorage>();
        fs.ReadWithEtag(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((StoredObject?)null);
        fs.WriteIfMatch(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Throws(_ => new ConcurrencyConflictException("path", null, "someone-else-wrote"));

        var sut = new FeedSchemaManager(fs);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            sut.EnsureSchema(AssetDir("BTCUSDT_PersistConflict"), "feed-x", "1m", ["ts"], ct: Ct));

        await fs.Received(5).WriteIfMatch(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancellationBetweenAttempts_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var fs = Substitute.For<IFileStorage>();
        fs.ReadWithEtag(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((StoredObject?)null);
        fs.WriteIfMatch(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Throws(_ =>
            {
                cts.Cancel();
                return new ConcurrencyConflictException("path", null, "actual");
            });

        var sut = new FeedSchemaManager(fs);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.EnsureSchema(AssetDir("BTCUSDT_CancelInLoop"), "feed-x", "1m", ["ts"], ct: cts.Token));
    }

    [Fact]
    public async Task ConflictThenSuccess_Retries_AndEventFiresOnce()
    {
        var fs = Substitute.For<IFileStorage>();
        fs.ReadWithEtag(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((StoredObject?)null);

        var calls = 0;
        fs.WriteIfMatch(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                if (calls == 1) throw new ConcurrencyConflictException("path", null, "x");
                return Task.FromResult("new-etag");
            });

        var sut = new FeedSchemaManager(fs);
        var events = new List<string>();
        sut.ManifestChanged += events.Add;

        await sut.EnsureSchema(AssetDir("BTCUSDT_Retry"), "feed-x", "1m", ["ts"], ct: Ct);

        Assert.Equal(2, calls);
        Assert.Single(events);
    }
}
```

- [ ] **Step 2: Run the new tests**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/AlgoTradeForge.HistoryLoader.Tests.csproj --filter "FullyQualifiedName~FeedSchemaManagerOptimisticConcurrencyTests"`
Expected: all 3 tests pass.

- [ ] **Step 3: Commit**

```bash
git add tests/AlgoTradeForge.HistoryLoader.Tests/Storage/FeedSchemaManagerOptimisticConcurrencyTests.cs
git commit -m "test(history): cover retry exhaustion + mid-loop cancellation + retry-then-success in FeedSchemaManager"
```

---

## Task 9 — Full-suite verification + manual smoke

Belt-and-braces verification before declaring done.

- [ ] **Step 1: Build the whole solution clean**

Run: `dotnet build AlgoTradeForge.slnx --no-incremental`
Expected: 0 errors, 0 warnings (or no new warnings beyond what existed pre-change).

- [ ] **Step 2: Run the four relevant test projects sequentially**

Per CLAUDE.md: never run `dotnet` projects in parallel. Run each project's tests in sequence:

```
dotnet test tests/AlgoTradeForge.Domain.Tests/AlgoTradeForge.Domain.Tests.csproj
dotnet test tests/AlgoTradeForge.Application.Tests/AlgoTradeForge.Application.Tests.csproj
dotnet test tests/AlgoTradeForge.Infrastructure.Tests/AlgoTradeForge.Infrastructure.Tests.csproj
dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/AlgoTradeForge.HistoryLoader.Tests.csproj
```
Expected: all pass.

- [ ] **Step 3: Manual smoke — reproduce the original symptom is gone**

This is the test that the *user-visible* bug is fixed. Use the dev environment (VS Code launch settings per `reference_vscode_launch.md` — ports 5000/5051/3000, test DataRoot `HistoryTest`).

1. Start the HistoryLoader WebApi.
2. Start the main WebApi.
3. Start the frontend dev server.
4. Open the Data tab in the browser.
5. Click "+" on `EqV_1m_5M` for `BTCUSDT_perp`. The Feed status side panel should populate (no perpetual "Loading status…" stuck state).
6. Close the panel, rapid-click "+" several times in succession on the same and adjacent feed cells.

Expected:
- **No** Visual Studio first-chance exception popup for `OperationCanceledException` originating from `SemaphoreSlim.WaitUntilCountOrTimeoutAsync` in the `FeedSchemaManager.Load` path.
- **No** stuck "Loading status…" panel — every panel either populates with feed JSON or shows a 404/error if the feed legitimately doesn't exist.
- **No** stuck loading on the aggregation-options call (drives the eligibility banner / "+" button state).

Do not commit anything from this step — it's verification only.

- [ ] **Step 4: Final summary commit (optional, only if you have polish to add)**

If everything passed and there's nothing left to change, you're done. If there's a tiny polish (a comment removed, a stray `using` cleaned up), do it now and commit as a single tidy-up.

---

## Out of scope (deliberate — do not touch in this plan)

- **`AppSettingsWriter` migration** to the same primitive. Same shape, low contention. Leave for a follow-up PR if needed.
- **Cross-process `LocalFileStorage` safety.** The internal per-key `SemaphoreSlim` in `LocalFileStorage` makes the same single-process assumption the current code makes.
- **`FeedCatalog.GetAsset` cache reuse via `GetAssetsByExchange`.** Orthogonal efficiency win; flagged in the spec but out of scope.
- **`WriteLockManager`, `RunProgressCache._keyLocks`, `BinanceLiveAccountManager._accountLocks`.** Each guards a different invariant unrelated to blob CAS.

---

## Self-review (run before handing off to execution)

**Spec coverage:**
- ✅ Motivation / symptom → addressed in Task 9 smoke.
- ✅ Remove `_locks` from `FeedSchemaManager` → Task 7.
- ✅ Two new `IFileStorage` primitives → Tasks 2, 3, 4, 6.
- ✅ Bounded retry with jittered backoff in the manager → Task 7 (and verified in Task 8).
- ✅ Local FS ETag = SHA-1 hex → Task 3.
- ✅ Writer-side mutex (internal) → Task 4.
- ✅ `FileShare.Delete` on readers → Task 5.
- ✅ `Load` keeps its `CancellationToken` parameter → Task 7.
- ✅ S3 implementation via native `If-Match` → Task 6.
- ✅ Tests for ETag + Conditional contract → Tasks 3, 4, 5.
- ✅ Retry exhaustion test → Task 8.
- ✅ Mid-loop cancellation test → Task 8.
- ✅ Reader-vs-renamer regression test → Task 5.
- ✅ Canary parallel-writers test → already exists (`FeedSchemaManagerStressTests`), exercised in Task 7's Step 4.

**Placeholder scan:** no "TBD", "TODO", "similar to …" — every code block is the actual code to write.

**Type consistency:**
- `StoredObject(string Content, string Etag)` consistent across tasks 1, 3, 6, 8.
- `ConcurrencyConflictException(string key, string? expectedEtag, string? actualEtag)` consistent across tasks 1, 4, 6, 8.
- `LoadResult(FeedMetadata? Metadata, string? Etag)` consistent within Task 7.
- `UpdateWithRetry(path, mutator, ct)` signature consistent within Task 7 (returns `Task<bool>` everywhere).
- `MaxAttempts = 5`; backoff scales `1 << attempt` for `attempt ∈ [0, 4)`; the 5th attempt has no following delay. Consistent between Task 7's `UpdateWithRetry` and Task 8's `Received(5)` assertion.
