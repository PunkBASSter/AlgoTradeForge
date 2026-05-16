using System.Text;

namespace AlgoTradeForge.Application.IO;

/// <summary>
/// Async storage routing to local FS or S3. Keys are slash-delimited; the local backend
/// resolves them against <see cref="LocalStorageOptions.DataRoot"/> and passes absolute
/// paths through for legacy callers. No <c>Async</c> suffix by convention.
/// </summary>
public interface IFileStorage
{
    Task<bool> Exists(string key, CancellationToken ct = default);

    IAsyncEnumerable<string> ListKeys(string prefix, string? suffix = null, bool recursive = true, CancellationToken ct = default);

    Task<Stream> OpenRead(string key, CancellationToken ct = default);
    Task<string> ReadAllText(string key, CancellationToken ct = default);
    Task<string[]> ReadAllLines(string key, CancellationToken ct = default);
    IAsyncEnumerable<string> ReadLines(string key, CancellationToken ct = default);
    Task<byte[]> ReadAllBytes(string key, CancellationToken ct = default);

    /// <summary>Atomic replace of any existing object at the key.</summary>
    Task WriteAllText(string key, string content, Encoding? encoding = null, CancellationToken ct = default);
    Task WriteAllLines(string key, IEnumerable<string> lines, CancellationToken ct = default);
    Task WriteAllBytes(string key, ReadOnlyMemory<byte> bytes, CancellationToken ct = default);

    /// <summary>Callers MUST call <see cref="IObjectWriteSession.Commit"/>; disposing without commit aborts.</summary>
    Task<IObjectWriteSession> OpenWriteSession(string key, CancellationToken ct = default);

    Task Delete(string key, CancellationToken ct = default);
    Task DeleteByPrefix(string prefix, CancellationToken ct = default);

    /// <summary>Atomic rename on local; copy+delete (not atomic) on object stores.</summary>
    Task Move(string sourceKey, string destinationKey, bool overwrite, CancellationToken ct = default);
}
