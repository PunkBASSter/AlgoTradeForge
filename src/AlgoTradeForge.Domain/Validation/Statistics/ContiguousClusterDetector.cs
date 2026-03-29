namespace AlgoTradeForge.Domain.Validation.Statistics;

/// <summary>
/// Finds the largest contiguous rectangle in a boolean grid where enough cells pass.
/// Used by WFM to identify stable regions across period count / OOS percentage configurations.
/// </summary>
public static class ContiguousClusterDetector
{
    /// <summary>
    /// Scans all rectangles ≥ (minRows × minCols) and returns the largest one where
    /// passing cells ≥ minCellsPassing. For a 6×3 grid this is ~50 rectangles — trivially fast.
    /// </summary>
    /// <returns>The largest qualifying cluster, or null if none meets the criteria.</returns>
    public static (int Row, int Col, int Rows, int Cols)? FindLargestCluster(
        bool[,] grid, int minRows, int minCols, int minCellsPassing)
    {
        var rows = grid.GetLength(0);
        var cols = grid.GetLength(1);

        // Build prefix sum for O(1) rectangle queries
        var prefix = new int[rows + 1, cols + 1];
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
                prefix[r + 1, c + 1] = (grid[r, c] ? 1 : 0)
                    + prefix[r, c + 1] + prefix[r + 1, c] - prefix[r, c];

        (int Row, int Col, int Rows, int Cols)? best = null;
        var bestArea = 0;

        // Enumerate all rectangles ≥ minRows × minCols
        for (var r1 = 0; r1 < rows; r1++)
        {
            for (var c1 = 0; c1 < cols; c1++)
            {
                for (var r2 = r1 + minRows - 1; r2 < rows; r2++)
                {
                    for (var c2 = c1 + minCols - 1; c2 < cols; c2++)
                    {
                        var area = (r2 - r1 + 1) * (c2 - c1 + 1);
                        if (area <= bestArea) continue;

                        // Count passing cells via prefix sum
                        var passing = prefix[r2 + 1, c2 + 1]
                            - prefix[r1, c2 + 1]
                            - prefix[r2 + 1, c1]
                            + prefix[r1, c1];

                        if (passing >= minCellsPassing)
                        {
                            best = (r1, c1, r2 - r1 + 1, c2 - c1 + 1);
                            bestArea = area;
                        }
                    }
                }
            }
        }

        return best;
    }
}
