namespace AlgoTradeForge.HistoryLoader.Application.Archive;

public sealed class ArchiveIntegrityException : Exception
{
    public ArchiveIntegrityException(string url)
        : base($"Archive checksum mismatch after retry: {url}") { }

    private ArchiveIntegrityException(string message, Exception? inner)
        : base(message, inner) { }

    public static ArchiveIntegrityException NonMonotonicArchive(string detail)
        => new($"Non-monotonic archive timestamps: {detail}", inner: null);
}
