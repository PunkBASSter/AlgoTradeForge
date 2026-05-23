using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Storage;

/// <summary>
/// S3 <see cref="IFileStorage"/>. Keys are stored under
/// <see cref="S3StorageOptions.KeyPrefix"/>. Writes are atomic-by-PUT (one
/// <c>PutObject</c> per object); <see cref="Move"/> is copy+delete and therefore
/// best-effort (interface contract). Absolute keys are rejected — that bypass is
/// local-FS only (see <c>LocalFileStorage.Resolve</c>).
/// </summary>
public sealed class S3FileStorage : IFileStorage, IDisposable
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly string _keyPrefix;
    private readonly ILogger<S3FileStorage> _logger;
    private readonly bool _ownsClient;

    public S3FileStorage(S3StorageOptions options, ILogger<S3FileStorage> logger)
        : this(BuildClient(options), options, logger, ownsClient: true) { }

    public S3FileStorage(IOptions<StorageOptions> options, ILogger<S3FileStorage> logger)
        : this(options.Value.S3, logger) { }

    internal S3FileStorage(IAmazonS3 client, S3StorageOptions options, ILogger<S3FileStorage> logger, bool ownsClient)
    {
        if (string.IsNullOrWhiteSpace(options.Bucket))
            throw new ArgumentException("Storage:S3:Bucket must be set when Backend=S3", nameof(options));
        _client = client;
        _bucket = options.Bucket;
        _keyPrefix = NormalizePrefix(options.KeyPrefix);
        _logger = logger;
        _ownsClient = ownsClient;
    }

    private static string NormalizePrefix(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var trimmed = raw.TrimStart('/');
        if (trimmed.Length == 0) return "";
        return trimmed.EndsWith('/') ? trimmed : trimmed + "/";
    }

    private static IAmazonS3 BuildClient(S3StorageOptions opt)
    {
        var config = new AmazonS3Config();
        if (!string.IsNullOrWhiteSpace(opt.Endpoint))
        {
            // Hetzner Object Storage (default), Ceph RGW, MinIO, R2, any other S3-compatible:
            // path-style + custom URL. AuthenticationRegion satisfies SigV4 even when
            // ServiceURL isn't a real AWS host. Hetzner enforces the region label in the
            // signature — must match the endpoint subdomain (fsn1 / nbg1 / hel1).
            config.ServiceURL = opt.Endpoint;
            config.ForcePathStyle = true;
            if (!string.IsNullOrWhiteSpace(opt.Region))
                config.AuthenticationRegion = opt.Region;
        }
        else if (!string.IsNullOrWhiteSpace(opt.Region))
        {
            // AWS opt-in branch: caller cleared Endpoint to ""; Region must be a real AWS
            // region (e.g. us-east-1). Non-AWS labels here will throw from RegionEndpoint.
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(opt.Region);
        }

        if (!string.IsNullOrWhiteSpace(opt.AccessKeyId) && !string.IsNullOrWhiteSpace(opt.SecretAccessKey))
            return new AmazonS3Client(new BasicAWSCredentials(opt.AccessKeyId, opt.SecretAccessKey), config);

        return new AmazonS3Client(config);
    }

    private string ToS3Key(string key)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("key must not be empty", nameof(key));
        if (Path.IsPathRooted(key))
            throw new ArgumentException($"Absolute paths are not supported on S3 storage; key='{key}'", nameof(key));
        var normalized = key.Replace('\\', '/').TrimStart('/');
        return _keyPrefix + normalized;
    }

    /// <summary>
    /// Like <see cref="ToS3Key"/> but allows the empty prefix to mean "everything under
    /// <see cref="_keyPrefix"/>" — needed for <see cref="ListKeys"/> and
    /// <see cref="DeleteByPrefix"/> so a caller can sweep the whole scoped namespace
    /// (e.g. test-suite teardown) without inventing a sentinel key.
    /// </summary>
    private string ToS3ListPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return _keyPrefix;
        if (Path.IsPathRooted(prefix))
            throw new ArgumentException($"Absolute paths are not supported on S3 storage; prefix='{prefix}'", nameof(prefix));
        var normalized = prefix.Replace('\\', '/').TrimStart('/');
        return _keyPrefix + normalized;
    }

    private string FromS3Key(string s3Key)
        => _keyPrefix.Length > 0 && s3Key.StartsWith(_keyPrefix, StringComparison.Ordinal)
            ? s3Key.Substring(_keyPrefix.Length)
            : s3Key;

    // S3 wraps ETags in double-quotes on the wire (per RFC 7232); strip them for the opaque value.
    private static string? StripQuotes(string? etag) =>
        string.IsNullOrEmpty(etag) ? etag :
        etag.Length >= 2 && etag[0] == '"' && etag[^1] == '"' ? etag[1..^1] : etag;

    private async Task<string?> TryGetCurrentEtag(string s3Key, CancellationToken ct)
    {
        try
        {
            var meta = await _client.GetObjectMetadataAsync(_bucket, s3Key, ct);
            return StripQuotes(meta.ETag);
        }
        catch (AmazonS3Exception ex) when (
            ex.StatusCode == HttpStatusCode.NotFound ||
            ex.StatusCode == HttpStatusCode.Forbidden)
        {
            if (ex.StatusCode == HttpStatusCode.Forbidden)
                _logger.LogWarning(ex, "S3 returned 403 on TryGetCurrentEtag('{Key}') — treating as missing.", s3Key);
            return null;
        }
    }

    public async Task<bool> Exists(string key, CancellationToken ct = default)
    {
        try
        {
            await _client.GetObjectMetadataAsync(_bucket, ToS3Key(key), ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            // S3 returns 403 (not 404) for HEAD on a missing key when the caller lacks
            // s3:ListBucket. Conflating "missing" and "access-denied" matches the IFileStorage
            // contract (false ⇒ "cannot be read from"), but log it so a real auth misconfig
            // surfaces in startup logs rather than silently breaking writes downstream.
            _logger.LogWarning(ex, "S3 returned 403 on Exists('{Key}') — treating as missing. Verify s3:ListBucket / s3:GetObject permissions if writes also fail.", key);
            return false;
        }
    }

    public async IAsyncEnumerable<string> ListKeys(
        string prefix,
        string? suffix = null,
        bool recursive = true,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var s3Prefix = ToS3ListPrefix(prefix);
        // Local FS treats "list a key that maps to a directory" the same as "list keys under that
        // prefix". The local impl appends '/' implicitly via Path.Combine. Mirror that here so a
        // caller passing the prefix without a trailing '/' still gets the same keys.
        if (s3Prefix.Length > 0 && !s3Prefix.EndsWith('/')) s3Prefix += "/";

        var request = new ListObjectsV2Request
        {
            BucketName = _bucket,
            Prefix = s3Prefix,
            Delimiter = recursive ? null : "/",
        };

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var response = await _client.ListObjectsV2Async(request, ct);
            if (response.S3Objects is not null)
            {
                foreach (var obj in response.S3Objects)
                {
                    if (obj.Key.EndsWith('/')) continue; // S3 "folder marker" — skip
                    if (suffix is not null && !obj.Key.EndsWith(suffix, StringComparison.Ordinal)) continue;
                    yield return FromS3Key(obj.Key);
                }
            }
            // CommonPrefixes (subdirectory pseudo-entries) intentionally ignored — Local FS
            // ListKeys(recursive:false) returns files only, not directories.
            if (response.IsTruncated != true) break;
            request.ContinuationToken = response.NextContinuationToken;
        }
    }

    public async Task<Stream> OpenRead(string key, CancellationToken ct = default)
    {
        var response = await _client.GetObjectAsync(_bucket, ToS3Key(key), ct);
        return new S3ResponseStream(response);
    }

    public async Task<string> ReadAllText(string key, CancellationToken ct = default)
    {
        await using var stream = await OpenRead(key, ct);
        using var reader = new StreamReader(stream);
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
        await using var stream = await OpenRead(key, ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
            yield return line;
    }

    public async Task<byte[]> ReadAllBytes(string key, CancellationToken ct = default)
    {
        await using var stream = await OpenRead(key, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    public async Task WriteAllText(string key, string content, Encoding? encoding = null, CancellationToken ct = default)
    {
        var enc = encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        await PutBytes(key, enc.GetBytes(content), ct);
    }

    public async Task WriteAllLines(string key, IEnumerable<string> lines, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        var writer = new StreamWriter(ms, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(line.AsMemory(), ct);
        }
        await writer.FlushAsync(ct);
        ms.Position = 0;
        await PutStream(key, ms, ct);
    }

    public async Task WriteAllBytes(string key, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        // PutObjectAsync requires a Stream; reuse PutBytes for the array copy.
        await PutBytes(key, bytes.ToArray(), ct);
    }

    public Task<IObjectWriteSession> OpenWriteSession(string key, CancellationToken ct = default)
    {
        IObjectWriteSession session = new S3WriteSession(_client, _bucket, ToS3Key(key));
        return Task.FromResult(session);
    }

    public async Task Delete(string key, CancellationToken ct = default)
    {
        try
        {
            await _client.DeleteObjectAsync(_bucket, ToS3Key(key), ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Idempotent contract.
        }
    }

    public async Task DeleteByPrefix(string prefix, CancellationToken ct = default)
    {
        var s3Prefix = ToS3ListPrefix(prefix);
        if (s3Prefix.Length > 0 && !s3Prefix.EndsWith('/')) s3Prefix += "/";

        var listRequest = new ListObjectsV2Request { BucketName = _bucket, Prefix = s3Prefix };
        var batch = new List<KeyVersion>(capacity: 1000);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var response = await _client.ListObjectsV2Async(listRequest, ct);
            if (response.S3Objects is not null)
            {
                foreach (var obj in response.S3Objects)
                {
                    batch.Add(new KeyVersion { Key = obj.Key });
                    if (batch.Count >= 1000)
                    {
                        await FlushDeleteBatch(batch, ct);
                        batch.Clear();
                    }
                }
            }
            if (response.IsTruncated != true) break;
            listRequest.ContinuationToken = response.NextContinuationToken;
        }

        if (batch.Count > 0) await FlushDeleteBatch(batch, ct);
    }

    private async Task FlushDeleteBatch(List<KeyVersion> batch, CancellationToken ct)
    {
        var del = new DeleteObjectsRequest { BucketName = _bucket, Quiet = true };
        del.Objects.AddRange(batch);
        await _client.DeleteObjectsAsync(del, ct);
    }

    public async Task Move(string sourceKey, string destinationKey, bool overwrite, CancellationToken ct = default)
    {
        var src = ToS3Key(sourceKey);
        var dst = ToS3Key(destinationKey);

        // TODO: check-then-act race — a concurrent writer could land between Exists and
        // CopyObject. Current callers serialize via WriteLockManager, so harmless in practice.
        // Promoting to a server-side conditional copy depends on SDK + endpoint support
        // (S3 added If-None-Match on CopyObject in late 2024; MinIO coverage is uneven).
        if (!overwrite && await Exists(destinationKey, ct))
            throw new IOException($"Move target already exists: {destinationKey}");

        // Copy + delete — not atomic on S3. The interface contract acknowledges this.
        var copy = new CopyObjectRequest
        {
            SourceBucket = _bucket,
            SourceKey = src,
            DestinationBucket = _bucket,
            DestinationKey = dst,
        };
        await _client.CopyObjectAsync(copy, ct);
        await _client.DeleteObjectAsync(_bucket, src, ct);
    }

    /// <summary>
    /// Range-GET the trailing <paramref name="maxBytes"/> of <paramref name="key"/>; returns null
    /// if the object does not exist. Used by <see cref="S3TailIndex"/> as the S3 analogue of
    /// <see cref="LocalTailIndex"/>'s seek-from-end pattern.
    /// </summary>
    internal async Task<byte[]?> GetTail(string key, int maxBytes, CancellationToken ct)
    {
        if (maxBytes <= 0) return Array.Empty<byte>();
        var request = new GetObjectRequest
        {
            BucketName = _bucket,
            Key = ToS3Key(key),
            ByteRange = new ByteRange($"bytes=-{maxBytes}"),
        };
        try
        {
            using var response = await _client.GetObjectAsync(request, ct);
            await using var stream = response.ResponseStream;
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task PutBytes(string key, byte[] bytes, CancellationToken ct)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        await PutStream(key, ms, ct);
    }

    private Task PutStream(string key, Stream stream, CancellationToken ct)
    {
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = ToS3Key(key),
            InputStream = stream,
            AutoCloseStream = false,
            DisablePayloadSigning = false,
        };
        return _client.PutObjectAsync(request, ct);
    }

    public async Task<StoredObject?> ReadWithEtag(string key, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _client.GetObjectAsync(_bucket, ToS3Key(key), ct);
            using var reader = new StreamReader(resp.ResponseStream);
            var content = await reader.ReadToEndAsync(ct);
            return new StoredObject(content, StripQuotes(resp.ETag)!);
        }
        catch (AmazonS3Exception ex) when (
            ex.StatusCode == HttpStatusCode.NotFound ||
            ex.StatusCode == HttpStatusCode.Forbidden)
        {
            if (ex.StatusCode == HttpStatusCode.Forbidden)
                _logger.LogWarning(ex, "S3 returned 403 on ReadWithEtag('{Key}') — treating as missing. Verify s3:ListBucket / s3:GetObject permissions if writes also fail.", key);
            return null;
        }
    }

    public async Task<string> WriteIfMatch(string key, string content, string? expectedETag, CancellationToken ct = default)
    {
        var s3Key = ToS3Key(key);
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = s3Key,
            ContentBody = content,
            ContentType = "application/octet-stream",
        };

        if (expectedETag is null)
            request.IfNoneMatch = "*";
        else
            request.IfMatch = $"\"{expectedETag}\"";

        try
        {
            var resp = await _client.PutObjectAsync(request, ct);
            return StripQuotes(resp.ETag)!;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            var actual = await TryGetCurrentEtag(s3Key, ct);
            throw new ConcurrencyConflictException(key, expectedETag, actual, ex);
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    /// <summary>Owns the <see cref="GetObjectResponse"/> so disposing the stream disposes the HTTP response.</summary>
    private sealed class S3ResponseStream : Stream
    {
        private readonly GetObjectResponse _response;
        private readonly Stream _inner;

        public S3ResponseStream(GetObjectResponse response)
        {
            _response = response;
            _inner = response.ResponseStream;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _response.ContentLength;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _response.Dispose();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            _response.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Buffers writes in a <see cref="MemoryStream"/>; one <c>PutObject</c> on commit.</summary>
    private sealed class S3WriteSession : IObjectWriteSession
    {
        private readonly IAmazonS3 _client;
        private readonly string _bucket;
        private readonly string _s3Key;
        private MemoryStream? _buffer = new();
        private bool _committed;
        private bool _aborted;

        public S3WriteSession(IAmazonS3 client, string bucket, string s3Key)
        {
            _client = client;
            _bucket = bucket;
            _s3Key = s3Key;
        }

        public Stream Stream => _buffer ?? throw new ObjectDisposedException(nameof(S3WriteSession));

        public async Task Commit(CancellationToken ct = default)
        {
            if (_committed) return;
            if (_aborted) throw new InvalidOperationException("Cannot commit a session that was aborted.");
            if (_buffer is null) throw new ObjectDisposedException(nameof(S3WriteSession));

            _buffer.Position = 0;
            var request = new PutObjectRequest
            {
                BucketName = _bucket,
                Key = _s3Key,
                InputStream = _buffer,
                AutoCloseStream = false,
            };
            await _client.PutObjectAsync(request, ct);
            _buffer.Dispose();
            _buffer = null;
            _committed = true;
        }

        public Task Abort(CancellationToken ct = default)
        {
            if (_aborted || _committed) return Task.CompletedTask;
            _buffer?.Dispose();
            _buffer = null;
            _aborted = true;
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (_committed || _aborted) return;
            await Abort();
        }
    }
}
