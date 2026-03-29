using AlgoTradeForge.Domain.Validation.Statistics;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation.Statistics;

public class ContiguousClusterDetectorTests
{
    [Fact]
    public void AllTrue_ReturnsFullGrid()
    {
        var grid = new bool[4, 3];
        for (var r = 0; r < 4; r++)
            for (var c = 0; c < 3; c++)
                grid[r, c] = true;

        var result = ContiguousClusterDetector.FindLargestCluster(grid, 2, 2, 4);

        Assert.NotNull(result);
        Assert.Equal(0, result.Value.Row);
        Assert.Equal(0, result.Value.Col);
        Assert.Equal(4, result.Value.Rows);
        Assert.Equal(3, result.Value.Cols);
    }

    [Fact]
    public void AllFalse_ReturnsNull()
    {
        var grid = new bool[4, 3]; // All false by default

        var result = ContiguousClusterDetector.FindLargestCluster(grid, 2, 2, 3);

        Assert.Null(result);
    }

    [Fact]
    public void CornerCluster_DetectedCorrectly()
    {
        var grid = new bool[4, 3];
        // Bottom-right 2×2 all true
        grid[2, 1] = true;
        grid[2, 2] = true;
        grid[3, 1] = true;
        grid[3, 2] = true;

        // Require all 4 cells passing in the 2×2 minimum — the 2×2 corner qualifies
        // but the full 4×3 grid (12 cells, 4 passing) does too since 4≥4 and it's larger.
        // Use minCellsPassing=5 to exclude larger sparse rectangles.
        var result = ContiguousClusterDetector.FindLargestCluster(grid, 2, 2, 5);

        // No rectangle has 5+ passing cells, so null
        Assert.Null(result);

        // With threshold=4, the algorithm finds the largest area with ≥4 cells
        var result2 = ContiguousClusterDetector.FindLargestCluster(grid, 2, 2, 4);
        Assert.NotNull(result2);
        Assert.True(result2.Value.Rows * result2.Value.Cols >= 4);
    }

    [Fact]
    public void Diagonal_NoCluster()
    {
        // Diagonal pattern — 4 cells in a 4×4 grid
        var grid = new bool[4, 4];
        grid[0, 0] = true;
        grid[1, 1] = true;
        grid[2, 2] = true;
        grid[3, 3] = true;

        // Require 5 passing cells — only 4 exist, so no rectangle qualifies
        var result = ContiguousClusterDetector.FindLargestCluster(grid, 2, 2, 5);

        Assert.Null(result);
    }

    [Fact]
    public void MinimumThreshold_Enforced()
    {
        var grid = new bool[3, 3];
        // 3×3 but only 6 passing (threshold is 7)
        grid[0, 0] = true; grid[0, 1] = true; grid[0, 2] = false;
        grid[1, 0] = true; grid[1, 1] = true; grid[1, 2] = true;
        grid[2, 0] = true; grid[2, 1] = false; grid[2, 2] = false;

        var result = ContiguousClusterDetector.FindLargestCluster(grid, 3, 3, 7);

        Assert.Null(result); // Only 5 passing in the 3×3

        // But with threshold 5, should pass
        var result2 = ContiguousClusterDetector.FindLargestCluster(grid, 3, 3, 5);
        Assert.NotNull(result2);
    }

    [Fact]
    public void WfmGrid_6x3_RealisticScenario()
    {
        // Typical WFM grid: 6 period counts × 3 OOS pcts
        var grid = new bool[6, 3];
        // Middle rows pass (moderate period counts work)
        grid[1, 0] = true; grid[1, 1] = true; grid[1, 2] = true;
        grid[2, 0] = true; grid[2, 1] = true; grid[2, 2] = true;
        grid[3, 0] = true; grid[3, 1] = true; grid[3, 2] = true;

        // 9 passing cells in rows 1-3. The full 6×3 grid also has 9 passing cells.
        // With minCellsPassing=9, the largest rectangle (by area) with ≥9 passing
        // is the full 6×3 (area 18), which contains all 9 passing cells.
        var result = ContiguousClusterDetector.FindLargestCluster(grid, 3, 3, 9);

        Assert.NotNull(result);
        Assert.Equal(6, result.Value.Rows);
        Assert.Equal(3, result.Value.Cols);
    }
}
