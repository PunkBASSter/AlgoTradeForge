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
