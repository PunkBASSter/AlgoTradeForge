namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Pages IB historical "TRADES" ticks into one contiguous [fromMs, toMs) list, de-duplicating the per-page
// overlap that IB's SECOND-granular startDateTime forces: each page re-returns the whole boundary second, so a
// naive `cursor = lastMs + 1` re-rounds to that same second and duplicates it (and, if a second holds >pageSize
// ticks, a "no forward progress" break silently drops the remainder). This pager carries a per-second skip count
// so the overlap is dropped and a huge second is walked across pages.
//
// Pure — takes a page-fetcher delegate — so the dedup is unit-tested without a socket. The dedup ASSUMES IB
// returns a given second's ticks in a STABLE order across requests; that assumption is unvalidated against a live
// entitled feed (paper hits 10189), so verify end-to-end when historical backfill goes live (Plan 4).
//
// KNOWN LIMIT: IB's startDateTime is second-granular, so a single second holding more than `pageSize` ticks cannot
// be paged through forward — every request returns that second's same first `pageSize`. The pager escapes such a
// second (advancing past it) rather than looping forever or dropping all later seconds; the overflow within that
// one second is unrecoverable via this API. >pageSize trades in one second is extreme; documented, not silently dropped.
internal static class HistoricalTickPager
{
    public static async Task<IReadOnlyList<IbHistoricalTick>> Collect(
        Func<long, CancellationToken, Task<IReadOnlyList<IbHistoricalTick>>> fetchPage,
        long fromMs, long toMs, int pageSize, CancellationToken ct = default)
    {
        var all = new List<IbHistoricalTick>();
        var cursorSec = fromMs / 1000;   // IB startDateTime is second-granular
        var takenAtCursorSec = 0;        // ticks at cursorSec already emitted on prior pages (overlap dedup)

        while (cursorSec * 1000 < toMs)
        {
            ct.ThrowIfCancellationRequested();
            var page = await fetchPage(cursorSec, ct).ConfigureAwait(false);
            if (page.Count == 0) break;

            var seenAtCursor = 0;
            var added = 0;
            var reachedEnd = false;
            foreach (var t in page)
            {
                if (t.TimeSec == cursorSec)
                {
                    seenAtCursor++;
                    if (seenAtCursor <= takenAtCursorSec) continue; // already emitted on a prior page — skip the overlap
                }
                if (t.TimeSec * 1000 >= toMs) { reachedEnd = true; break; }
                all.Add(t);
                added++;
            }
            if (reachedEnd) break;

            var lastSec = page[^1].TimeSec;
            if (lastSec != cursorSec)
            {
                // Advanced to a new boundary second. Every tick at lastSec in this page was emitted (it is past
                // cursorSec), so that count is the next page's overlap-skip.
                takenAtCursorSec = CountAtSecond(page, lastSec);
                cursorSec = lastSec;
                if (page.Count < pageSize) break; // last (partial) page
            }
            else if (page.Count < pageSize)
            {
                break; // the whole (final) page is one second and IB returned fewer than a full page — range exhausted
            }
            else if (added > 0)
            {
                takenAtCursorSec = seenAtCursor; // full page, still within cursorSec, but we drained new ticks — keep going
            }
            else
            {
                // Full page, all of cursorSec, nothing new: IB cannot page further within this second. Skip to the
                // next second (the overflow within cursorSec is unrecoverable) so later seconds are not lost.
                cursorSec += 1;
                takenAtCursorSec = 0;
            }
        }
        return all;
    }

    private static int CountAtSecond(IReadOnlyList<IbHistoricalTick> page, long sec)
    {
        var n = 0;
        foreach (var t in page)
            if (t.TimeSec == sec) n++;
        return n;
    }
}
