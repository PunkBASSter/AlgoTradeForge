using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Storage;

/// <summary>
/// Local-FS <see cref="IFileStorage"/>. Absolute keys pass through; relative keys resolve
/// against <see cref="LocalStorageOptions.DataRoot"/>. Writes atomic via <c>.tmp</c> +
/// <see cref="File.Move(string, string, bool)"/>. Streams open with <c>useAsync: true</c>.
/// Concurrent writers on the same key race on the temp file and final move — callers must
/// serialize via domain-level locks (e.g. <c>WriteLockManager</c>) when that matters.
/// </summary>
public sealed class LocalFileStorage : IFileStorage
{
    private const int DefaultBufferSize = 4096;
    private readonly string _dataRoot;

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
        // TODO PR 4b/4c: remove absolute-path bypass once aggregation/event call sites use
        // StorageKeys. AppSettingsWriter intentionally relies on this bypass — its target is
        // the host content-root appsettings.json (not data-root content) and must keep an
        // absolute path even after the bypass goes away.
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
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, DefaultBufferSize, useAsync: true);
        return Task.FromResult(stream);
    }

    public async Task<string> ReadAllText(string key, CancellationToken ct = default)
    {
        await using var fs = new FileStream(Resolve(key), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, DefaultBufferSize, useAsync: true);
        using var reader = new StreamReader(fs);
        return await reader.ReadToEndAsync(ct);
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
        await using var fs = new FileStream(Resolve(key), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, DefaultBufferSize, useAsync: true);
        using var reader = new StreamReader(fs);
        while (await reader.ReadLineAsync(ct) is { } line)
            yield return line;
    }

    public Task<byte[]> ReadAllBytes(string key, CancellationToken ct = default)
        => File.ReadAllBytesAsync(Resolve(key), ct);

    public async Task WriteAllText(string key, string content, Encoding? encoding = null, CancellationToken ct = default)
    {
        await using var session = await OpenWriteSession(key, ct);
        var enc = encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var bytes = enc.GetBytes(content);
        await session.Stream.WriteAsync(bytes, ct);
        await session.Commit(ct);
    }

    public async Task WriteAllLines(string key, IEnumerable<string> lines, CancellationToken ct = default)
    {
        await using var session = await OpenWriteSession(key, ct);
        var writer = new StreamWriter(session.Stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(line.AsMemory(), ct);
        }
        await writer.FlushAsync(ct);
        await session.Commit(ct);
    }

    public async Task WriteAllBytes(string key, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        await using var session = await OpenWriteSession(key, ct);
        await session.Stream.WriteAsync(bytes, ct);
        await session.Commit(ct);
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

            await _stream.FlushAsync(ct);
            // Drive contents past the OS cache before the rename. Without flushToDisk:true, NTFS
            // can commit the new file length on unclean shutdown but leave the data pages zeroed,
            // producing a same-size, all-zero file that crashes the next deserialize.
            _stream.Flush(flushToDisk: true);
            await _stream.DisposeAsync();
            _stream = null;
            File.Move(_tmpPath, _finalPath, overwrite: true);
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
