namespace AlgoTradeForge.Storage;

public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(string key, string? expectedETag, string? actualETag)
        : this(key, expectedETag, actualETag, innerException: null) { }

    public ConcurrencyConflictException(string key, string? expectedETag, string? actualETag, Exception? innerException)
        : base(
            $"Concurrency conflict on '{key}': expected etag '{expectedETag ?? "<absent>"}', actual '{actualETag ?? "<absent>"}'.",
            innerException)
    {
        Key = key;
        ExpectedETag = expectedETag;
        ActualETag = actualETag;
    }

    public string Key { get; }
    public string? ExpectedETag { get; }
    public string? ActualETag { get; }
}
