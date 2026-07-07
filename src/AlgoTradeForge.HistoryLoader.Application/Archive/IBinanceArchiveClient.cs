namespace AlgoTradeForge.HistoryLoader.Application.Archive;

public interface IBinanceArchiveClient
{
    // Returns the extracted CSV stream (temp-file backed, auto-deleted on dispose), or null on 404.
    // Throws ArchiveIntegrityException after one failed re-download on checksum mismatch.
    Task<Stream?> DownloadMonthly(string market, string dataset, string symbol, string? interval, int year, int month, CancellationToken ct = default);
    Task<Stream?> DownloadDaily(string market, string dataset, string symbol, string? interval, DateOnly date, CancellationToken ct = default);
}
