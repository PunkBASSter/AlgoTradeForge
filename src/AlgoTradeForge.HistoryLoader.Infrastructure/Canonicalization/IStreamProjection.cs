using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

/// <summary>
/// Per-type consumer slice: decodes one relay frame and writes its canonical row through the
/// stream's existing CSV sink. Mirrors the producer's IFramePayload&lt;T&gt; — adding a stream
/// type is a new projection + one registration, no edits to the tail loop.
/// </summary>
public interface IStreamProjection<T> where T : IFramePayload<T>
{
    void Apply(in T frame, in SegmentHeader header, SegmentLocation loc);
    Task Seed(SegmentLocation loc, CancellationToken ct);   // seed the writer dedup watermark
    Task Flush(CancellationToken ct);                        // durable publish before cursor advance
}
