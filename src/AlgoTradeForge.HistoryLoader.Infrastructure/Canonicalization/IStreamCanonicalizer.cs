namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

/// <summary>Non-generic seam so the dispatcher can hold a set of typed canonicalizers and route
/// by <see cref="StreamName"/> without knowing the payload type.</summary>
public interface IStreamCanonicalizer
{
    string StreamName { get; }
    Task<int> Run(string venue, string instrumentOrVenue, CancellationToken ct = default);
}
