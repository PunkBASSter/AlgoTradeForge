namespace AlgoTradeForge.HistoryLoader.Application.Archive;

public sealed class ArchiveIntegrityException(string url)
    : Exception($"Archive checksum mismatch after retry: {url}");
