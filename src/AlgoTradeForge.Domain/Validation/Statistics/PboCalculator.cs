using AlgoTradeForge.Domain.Validation.Results;

namespace AlgoTradeForge.Domain.Validation.Statistics;

/// <summary>
/// Combinatorially Symmetric Cross-Validation (CSCV) calculator that produces the
/// Probability of Backtest Overfitting (PBO). Partitions the returns into
/// S equal time-range blocks, enumerates all C(S, S/2) IS/OOS splits, and measures how
/// often the IS-optimal trial ranks below the OOS median.
/// </summary>
public static class PboCalculator
{
    /// <summary>
    /// Computes PBO from the simulation cache's P&amp;L matrix.
    /// Blocks are defined by timestamp range to support variable-length trials.
    /// </summary>
    /// <param name="cache">Simulation cache with per-bar P&amp;L deltas for all trials.</param>
    /// <param name="numBlocks">Number of equal time-range blocks S to partition into (default 16).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>PBO result with logit distribution across all C(S, S/2) combinations.</returns>
    public static PboResult Compute(SimulationCache cache, int numBlocks, CancellationToken ct = default)
    {
        if (cache.TrialCount < 2 || cache.MaxBarCount < numBlocks ||
            cache.MaxTimestamp <= cache.MinTimestamp)
        {
            return new PboResult
            {
                Pbo = 0.5,
                LogitDistribution = [],
                NumCombinations = 0,
                NumBlocks = numBlocks,
            };
        }

        var n = cache.TrialCount;
        var halfBlocks = numBlocks / 2;
        var totalDuration = cache.MaxTimestamp - cache.MinTimestamp;
        var blockDuration = totalDuration / numBlocks;

        if (blockDuration < 1)
        {
            return new PboResult
            {
                Pbo = 0.5,
                LogitDistribution = [],
                NumCombinations = 0,
                NumBlocks = numBlocks,
            };
        }

        // Pre-compute block windows per timeline (avoids N repeated binary searches for shared timelines)
        var timelineBlockWindows = new (int start, int length)[cache.TimelineCount][];
        for (var tl = 0; tl < cache.TimelineCount; tl++)
        {
            timelineBlockWindows[tl] = new (int, int)[numBlocks];
            for (var block = 0; block < numBlocks; block++)
            {
                var blockStartTs = cache.MinTimestamp + block * blockDuration;
                // long.MaxValue for last block: LowerBound returns array.Length, so FindTimelineWindow captures all remaining bars
                var blockEndTs = block == numBlocks - 1
                    ? long.MaxValue
                    : cache.MinTimestamp + (block + 1) * blockDuration;

                timelineBlockWindows[tl][block] = cache.FindTimelineWindow(tl, blockStartTs, blockEndTs);
            }
        }

        // Pre-compute per-trial per-block cumulative P&L using cached timeline windows
        var blockPnl = new double[n][];
        for (var trial = 0; trial < n; trial++)
        {
            blockPnl[trial] = new double[numBlocks];
            var windows = timelineBlockWindows[cache.GetTimelineIndex(trial)];
            for (var block = 0; block < numBlocks; block++)
            {
                var (start, len) = windows[block];
                var span = cache.GetTrialPnlWindow(trial, start, len);
                var sum = 0.0;
                for (var b = 0; b < span.Length; b++)
                    sum += span[b];
                blockPnl[trial][block] = sum;
            }
        }

        // Generate all C(numBlocks, halfBlocks) combinations
        var combinations = GenerateCombinations(numBlocks, halfBlocks);
        var numCombinations = combinations.Count;

        var logits = new double[numCombinations];
        var overfitFlags = new int[numCombinations]; // 1 if IS-optimal ranks below OOS median

        Parallel.For(0, numCombinations, new ParallelOptions { CancellationToken = ct }, comboIdx =>
        {
            var isBlocks = combinations[comboIdx];
            var isBlock = new bool[numBlocks];
            foreach (var b in isBlocks)
                isBlock[b] = true;

            // Compute IS and OOS total P&L per trial
            var isPerf = new double[n];
            var oosPerf = new double[n];
            for (var trial = 0; trial < n; trial++)
            {
                var isSum = 0.0;
                var oosSum = 0.0;
                for (var block = 0; block < numBlocks; block++)
                {
                    if (isBlock[block])
                        isSum += blockPnl[trial][block];
                    else
                        oosSum += blockPnl[trial][block];
                }
                isPerf[trial] = isSum;
                oosPerf[trial] = oosSum;
            }

            // Find IS-optimal trial
            var bestIsTrial = 0;
            for (var trial = 1; trial < n; trial++)
            {
                if (isPerf[trial] > isPerf[bestIsTrial])
                    bestIsTrial = trial;
            }

            // Rank IS-optimal trial by OOS performance (0 = worst, N-1 = best)
            var bestOosValue = oosPerf[bestIsTrial];
            var rank = 0;
            for (var trial = 0; trial < n; trial++)
            {
                if (oosPerf[trial] < bestOosValue)
                    rank++;
            }

            // Logit of relative rank: log(rank / (N - rank))
            // Clamp to avoid log(0): rank in [1, N-1]
            var clampedRank = Math.Clamp(rank, 1, n - 1);
            logits[comboIdx] = Math.Log((double)clampedRank / (n - clampedRank));

            // Overfit if IS-optimal ranks below OOS median (rank < N/2)
            overfitFlags[comboIdx] = rank < n / 2 ? 1 : 0;
        });

        var overfitCount = 0;
        for (var i = 0; i < numCombinations; i++)
            overfitCount += overfitFlags[i];

        return new PboResult
        {
            Pbo = (double)overfitCount / numCombinations,
            LogitDistribution = logits,
            NumCombinations = numCombinations,
            NumBlocks = numBlocks,
        };
    }

    /// <summary>
    /// Generates all C(n, k) combinations as a list of int arrays.
    /// Uses iterative lexicographic algorithm.
    /// </summary>
    internal static List<int[]> GenerateCombinations(int n, int k)
    {
        var result = new List<int[]>();
        var combo = new int[k];

        // Initialize first combination: [0, 1, 2, ..., k-1]
        for (var i = 0; i < k; i++)
            combo[i] = i;

        while (true)
        {
            result.Add((int[])combo.Clone());

            // Find rightmost element that can be incremented
            var pos = k - 1;
            while (pos >= 0 && combo[pos] == n - k + pos)
                pos--;

            if (pos < 0) break;

            combo[pos]++;
            for (var i = pos + 1; i < k; i++)
                combo[i] = combo[i - 1] + 1;
        }

        return result;
    }
}
