using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using AlgoTradeForge.Storage.Threading;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;

namespace AlgoTradeForge.Storage;

/// <summary>
/// Local-FS <see cref="IFileStorage"/>. Absolute keys pass through; relative keys resolve
/// against <see cref="LocalStorageOptions.DataRoot"/>. Writes atomic via <c>.tmp</c> +
/// <see cref="AtomicReplace"/> (POSIX-semantics rename on Windows / rename(2) on Unix — no absent
/// window even under concurrent readers). Streams open with <c>useAsync: true</c>. Concurrent writers on the same key race on the temp file
/// and final move — callers must serialize via domain-level locks (e.g. <c>WriteLockManager</c>)
/// when that matters. <see cref="WriteIfMatch"/> is the exception: it serializes its CAS-commit
/// critical section through a private per-key semaphore (<c>_writeLocks</c>), so multiple
/// <see cref="WriteIfMatch"/> calls on the same key are safe without external coordination.
/// <c>ct</c> propagation policy: honored on operations that can be slow or unbounded
/// (<see cref="ListKeys"/>, <see cref="DeleteByPrefix"/>, <see cref="WriteIfMatch"/>'s lock
/// acquire) so callers can abort them under load; intentionally NOT propagated to short
/// fast-path file reads/writes (<see cref="ReadAllText"/>, <see cref="ReadWithEtag"/>, etc.)
/// where the operation completes faster than the OS can deliver the cancel — propagating
/// there only surfaces first-chance breaks on routine client disconnects with no behavioral
/// benefit. S3FileStorage propagates <c>ct</c> everywhere because network latency makes
/// mid-call cancellation meaningful for every operation.
/// </summary>
public sealed class LocalFileStorage : IFileStorage
{
    private const int DefaultBufferSize = 4096;
    private readonly string _dataRoot;
    // Per-key CAS-commit serialization for WriteIfMatch. Unbounded by design: production
    // callers (FeedSchemaManager) have a bounded key set (one feeds.json per asset). Avoid
    // driving with an unbounded key set without adding eviction.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new();

    private SemaphoreSlim WriteLock(string fullPath) =>
        _writeLocks.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));

    public LocalFileStorage() : this(new LocalStorageOptions()) { }

    public LocalFileStorage(LocalStorageOptions options)
    {
        _dataRoot = options.DataRoot ?? "";
    }

    public LocalFileStorage(IOptions<StorageOptions> options)
        : this(options.Value.Local) { }

    private string Resolve(string key)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("key must not be empty", nameof(key));
        // TODO PR 4b/4c: remove absolute-path bypass once aggregation/event call sites use StorageKeys.
        if (Path.IsPathRooted(key)) return key;
        if (string.IsNullOrEmpty(_dataRoot)) return key;
        var native = key.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(_dataRoot, native);
    }

    public Task<bool> Exists(string key, CancellationToken ct = default)
        => Task.FromResult(File.Exists(Resolve(key)));

#pragma warning disable CS1998 // Local FS enumeration is synchronous; the async IAsyncEnumerable shape is what callers need.
    public async IAsyncEnumerable<string> ListKeys(
        string prefix,
        string? suffix = null,
        bool recursive = true,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var prefixPath = Resolve(prefix);
        var (rootDir, filter) = SplitPrefix(prefixPath);
        if (!Directory.Exists(rootDir)) yield break;

        var pattern = string.IsNullOrEmpty(filter) ? "*" : filter + "*";
        var search = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var file in Directory.EnumerateFiles(rootDir, pattern, search))
        {
            ct.ThrowIfCancellationRequested();
            if (suffix is not null && !file.EndsWith(suffix, StringComparison.Ordinal)) continue;
            yield return ToKey(file);
        }
    }
#pragma warning restore CS1998

    private static (string dir, string filter) SplitPrefix(string prefixPath)
    {
        if (Directory.Exists(prefixPath)) return (prefixPath, "");
        var dir = Path.GetDirectoryName(prefixPath);
        var name = Path.GetFileName(prefixPath);
        return (string.IsNullOrEmpty(dir) ? "." : dir, name ?? "");
    }

    private string ToKey(string absolutePath)
    {
        if (string.IsNullOrEmpty(_dataRoot)) return absolutePath.Replace(Path.DirectorySeparatorChar, '/');
        if (absolutePath.StartsWith(_dataRoot, StringComparison.OrdinalIgnoreCase))
        {
            var rel = absolutePath.Substring(_dataRoot.Length).TrimStart(Path.DirectorySeparatorChar, '/');
            return rel.Replace(Path.DirectorySeparatorChar, '/');
        }
        return absolutePath.Replace(Path.DirectorySeparatorChar, '/');
    }

    public Task<Stream> OpenRead(string key, CancellationToken ct = default)
    {
        var path = Resolve(key);
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, DefaultBufferSize, useAsync: true);
        return Task.FromResult(stream);
    }

    public async Task<string> ReadAllText(string key, CancellationToken ct = default)
    {
        await using var fs = new FileStream(Resolve(key), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, DefaultBufferSize, useAsync: true);
        using var reader = new StreamReader(fs);
        return await reader.ReadToEndAsync();
    }

    public async Task<string[]> ReadAllLines(string key, CancellationToken ct = default)
    {
        var lines = new List<string>();
        await foreach (var line in ReadLines(key, ct))
            lines.Add(line);
        return lines.ToArray();
    }

    public async IAsyncEnumerable<string> ReadLines(string key, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var fs = new FileStream(Resolve(key), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, DefaultBufferSize, useAsync: true);
        using var reader = new StreamReader(fs);
        while (await reader.ReadLineAsync() is { } line)
            yield return line;
    }

    public Task<byte[]> ReadAllBytes(string key, CancellationToken ct = default)
        => File.ReadAllBytesAsync(Resolve(key));

    public async Task WriteAllText(string key, string content, Encoding? encoding = null, CancellationToken ct = default)
    {
        await using var session = await OpenWriteSession(key, ct);
        var enc = encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var bytes = enc.GetBytes(content);
        await session.Stream.WriteAsync(bytes);
        await session.Commit();
    }

    public async Task WriteAllLines(string key, IEnumerable<string> lines, CancellationToken ct = default)
    {
        await using var session = await OpenWriteSession(key, ct);
        var writer = new StreamWriter(session.Stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        foreach (var line in lines)
        {
            await writer.WriteLineAsync(line.AsMemory());
        }
        await writer.FlushAsync();
        await session.Commit();
    }

    public async Task WriteAllBytes(string key, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        await using var session = await OpenWriteSession(key, ct);
        await session.Stream.WriteAsync(bytes);
        await session.Commit();
    }

    public Task<IObjectWriteSession> OpenWriteSession(string key, CancellationToken ct = default)
    {
        var finalPath = Resolve(key);
        var dir = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        IObjectWriteSession session = new LocalWriteSession(finalPath);
        return Task.FromResult(session);
    }

    public Task Delete(string key, CancellationToken ct = default)
    {
        var path = Resolve(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task DeleteByPrefix(string prefix, CancellationToken ct = default)
    {
        var prefixPath = Resolve(prefix);
        if (Directory.Exists(prefixPath))
        {
            Directory.Delete(prefixPath, recursive: true);
            return Task.CompletedTask;
        }
        var (rootDir, filter) = SplitPrefix(prefixPath);
        if (!Directory.Exists(rootDir)) return Task.CompletedTask;
        var pattern = string.IsNullOrEmpty(filter) ? "*" : filter + "*";
        foreach (var file in Directory.EnumerateFiles(rootDir, pattern, SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            File.Delete(file);
        }
        return Task.CompletedTask;
    }

    public Task Move(string sourceKey, string destinationKey, bool overwrite, CancellationToken ct = default)
    {
        var src = Resolve(sourceKey);
        var dst = Resolve(destinationKey);
        var dir = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.Move(src, dst, overwrite);
        return Task.CompletedTask;
    }

    public async Task<StoredObject?> ReadWithEtag(string key, CancellationToken ct = default)
    {
        var path = Resolve(key);
        if (!File.Exists(path)) return null;
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, DefaultBufferSize, useAsync: true);
        using var reader = new StreamReader(fs);
        var content = await reader.ReadToEndAsync();
        return new StoredObject(content, EtagOf(content));
    }

    private static string EtagOf(string content) => EtagOf(Encoding.UTF8.GetBytes(content));

    private static string EtagOf(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(bytes, hash);
        return Convert.ToHexString(hash);
    }

    // Replace dst with src with NO absent window, even while a concurrent reader holds dst open
    // (production readers open FileShare.ReadWrite|Delete). On Windows, File.Move(overwrite:true)
    // == MoveFileEx(REPLACE_EXISTING) throws UnauthorizedAccessException against an open dst (classic
    // rename semantics), so we issue a POSIX-semantics rename (FILE_RENAME_INFORMATION_EX) which
    // replaces atomically. On non-Windows, File.Move(overwrite:true) is rename(2) — already atomic.
    private static void AtomicReplace(string src, string dst)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(src, dst, overwrite: true);
            return;
        }
        try
        {
            WindowsPosixRename(src, dst);
        }
        catch (Win32Exception)
        {
            // POSIX rename unsupported (non-NTFS / pre-1809): classic move. Never fall back to
            // delete-then-move — that is the absent window this method exists to remove.
            File.Move(src, dst, overwrite: true);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void WindowsPosixRename(string src, string dst)
    {
        const uint DELETE = 0x00010000, GENERIC_WRITE = 0x40000000;
        const uint SHARE_ALL = 0x1 | 0x2 | 0x4;      // read | write | delete
        const uint OPEN_EXISTING = 3;
        const int  FileRenameInfoEx = 22;
        const uint REPLACE_IF_EXISTS = 0x1, POSIX_SEMANTICS = 0x2;

        using var handle = CreateFileW(src, DELETE | GENERIC_WRITE, SHARE_ALL,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());

        // FILE_RENAME_INFORMATION_EX { ULONG Flags; HANDLE RootDirectory; ULONG FileNameLength; WCHAR FileName[]; }
        // RootDirectory is pointer-aligned: on x64 Flags occupies [0,4) with [4,8) padding.
        var name = Encoding.Unicode.GetBytes(dst);
        int nameOffset = IntPtr.Size * 2 + sizeof(uint);   // 20 on x64, 12 on x86
        int size = nameOffset + name.Length + sizeof(char);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteInt32(buffer, 0, (int)(REPLACE_IF_EXISTS | POSIX_SEMANTICS));
            Marshal.WriteIntPtr(buffer, IntPtr.Size, IntPtr.Zero);          // RootDirectory
            Marshal.WriteInt32(buffer, IntPtr.Size * 2, name.Length);      // FileNameLength (bytes)
            Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
            Marshal.WriteInt16(buffer, nameOffset + name.Length, 0);
            if (!SetFileInformationByHandle(handle, FileRenameInfoEx, buffer, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess,
        uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle hFile,
        int fileInformationClass, IntPtr lpFileInformation, uint dwBufferSize);

    public async Task<string> WriteIfMatch(string key, string content, string? expectedETag, CancellationToken ct = default)
    {
        var path = Resolve(key);
        using var _ = await WriteLock(path).LockAsync(ct);

        var current = await ReadWithEtag(key, ct);
        if (current?.ETag != expectedETag)
            throw new ConcurrencyConflictException(key, expectedETag, current?.ETag);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                         FileShare.Read, DefaultBufferSize, useAsync: true))
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            await fs.WriteAsync(bytes);
            await fs.FlushAsync();
            fs.Flush(flushToDisk: true);
        }
        AtomicReplace(tmp, path);
        return EtagOf(content);
    }

    private sealed class LocalWriteSession : IObjectWriteSession
    {
        private readonly string _finalPath;
        private readonly string _tmpPath;
        private FileStream? _stream;
        private bool _committed;
        private bool _aborted;

        public LocalWriteSession(string finalPath)
        {
            _finalPath = finalPath;
            _tmpPath = finalPath + ".tmp";
            _stream = new FileStream(_tmpPath, FileMode.Create, FileAccess.Write, FileShare.Read, DefaultBufferSize, useAsync: true);
        }

        public Stream Stream => _stream ?? throw new ObjectDisposedException(nameof(LocalWriteSession));

        public async Task Commit(CancellationToken ct = default)
        {
            if (_committed) return;
            if (_aborted) throw new InvalidOperationException("Cannot commit a session that was aborted.");
            if (_stream is null) throw new ObjectDisposedException(nameof(LocalWriteSession));

            await _stream.FlushAsync();
            // Drive contents past the OS cache before the rename. Without flushToDisk:true, NTFS
            // can commit the new file length on unclean shutdown but leave the data pages zeroed,
            // producing a same-size, all-zero file that crashes the next deserialize.
            _stream.Flush(flushToDisk: true);
            await _stream.DisposeAsync();
            _stream = null;
            AtomicReplace(_tmpPath, _finalPath);
            _committed = true;
        }

        public async Task Abort(CancellationToken ct = default)
        {
            if (_aborted || _committed) return;
            if (_stream is not null)
            {
                await _stream.DisposeAsync();
                _stream = null;
            }
            if (File.Exists(_tmpPath)) File.Delete(_tmpPath);
            _aborted = true;
        }

        public async ValueTask DisposeAsync()
        {
            // Default-abort (not commit) so cancellation mid-write can't publish partial data.
            if (_committed || _aborted) return;
            await Abort();
        }
    }
}
