using System.Runtime.CompilerServices;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.LiveHost.Application.Live.Recovery;

/// <summary>
/// Drives <see cref="IReplaySource"/> through an <see cref="ICatchupGate"/> into one ordered,
/// deduped tick stream. On a gap it applies policy B: request backfill, and if the archive can't
/// bridge it within budget, declare a <see cref="Discontinuity"/> and resume from the new boundary.
/// The owning bar source feeds the yielded ticks into its accumulator (applying its own
/// suppress-known-bars rule); this type knows nothing about bars.
/// </summary>
public sealed class CatchupCoordinator(IReplaySource replay, IBackfillRequester backfill, RecoveryPolicy policy)
{
    public IAsyncEnumerable<TradeTick> StreamFromBoundary(
        ReplayRequest request,
        ICatchupGate gate,
        Action<Discontinuity> onDiscontinuity,
        CancellationToken ct = default)
        => Stream(request, gate, onDiscontinuity, lastAttemptedFromTs: long.MinValue, ct);

    // lastAttemptedFromTs keys the gap we last tried to backfill (by its low boundary), threaded
    // through the re-replay recursion so (a) DISTINCT later gaps each get their own attempt, and
    // (b) re-encountering the SAME gap — a backfill that reported success but did not actually
    // close it — is NOT retried (no infinite recursion); it declares instead.
    private async IAsyncEnumerable<TradeTick> Stream(
        ReplayRequest request,
        ICatchupGate gate,
        Action<Discontinuity> onDiscontinuity,
        long lastAttemptedFromTs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var tick in replay.Replay(request, ct).ConfigureAwait(false))
        {
            switch (gate.Admit(in tick))
            {
                case TickAdmission.Accept:
                    yield return tick;
                    break;
                case TickAdmission.Duplicate:
                    break;
                case TickAdmission.Gap:
                    // Time window from the last-good tick (gate.LastTimestampMs) to this one.
                    var gap = new Discontinuity(
                        gate.LastTimestampMs, tick.TimestampMs, DiscontinuityReason.MissingArchive);

                    // Attempt backfill once per distinct gap; budget zero short-circuits in the requester.
                    if (policy.BackfillBudget > TimeSpan.Zero
                        && gap.FromTs != lastAttemptedFromTs
                        && await backfill.TryBackfill(request, gap, policy, ct).ConfigureAwait(false))
                    {
                        // Bridge records are now archived but THIS enumerator is past them. Re-replay
                        // from the gap's low boundary (the gate dedupes the re-read prefix); a later
                        // distinct gap still gets its own attempt, the SAME gap does not.
                        await foreach (var b in Stream(
                            request with { FromTs = gap.FromTs }, gate, onDiscontinuity, gap.FromTs, ct).ConfigureAwait(false))
                            yield return b;
                        yield break;
                    }

                    onDiscontinuity(gap);
                    gate.Reseed(in tick);
                    yield return tick;
                    break;
            }
        }
    }
}
