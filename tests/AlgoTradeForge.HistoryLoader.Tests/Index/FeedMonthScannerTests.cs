using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Index;

public sealed class FeedMonthScannerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("atf-scan-").FullName;
    private readonly FeedMonthScanner _scanner = new();

    private string WriteCsv(string name, int dataRows)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllLines(path, new[] { "ts,o,h,l,c,v" }
            .Concat(Enumerable.Range(0, dataRows).Select(i => $"{i},1,1,1,1,1")));
        return path;
    }

    [Fact]
    public async Task Scan_CountsRowsExcludingHeader()
    {
        WriteCsv("2024-01_1h.csv", 744);
        WriteCsv("2024-02_1h.csv", 696);
        WriteCsv("2024-01_1m.csv", 10);      // other interval — ignored
        File.WriteAllText(Path.Combine(_dir, "status_1h.json"), "{}"); // non-partition — ignored

        var rows = await _scanner.Scan(_dir, "1h", new Dictionary<string, MonthPartitionRow>(), Ct);

        Assert.Equal(2, rows.Count);
        Assert.Equal(744, rows.Single(r => r.Month == "2024-01").Rows);
        Assert.Equal(696, rows.Single(r => r.Month == "2024-02").Rows);
    }

    [Fact]
    public async Task Scan_ReusesKnownCount_WhenLenAndMtimeMatch()
    {
        var path = WriteCsv("2024-01_1h.csv", 5);
        var fi = new FileInfo(path);
        var known = new Dictionary<string, MonthPartitionRow>
        {
            // Deliberately wrong count proves it was NOT recounted.
            ["2024-01"] = new("2024-01", 999, fi.Length, fi.LastWriteTimeUtc.ToString("O")),
        };

        var rows = await _scanner.Scan(_dir, "1h", known, Ct);
        Assert.Equal(999, Assert.Single(rows).Rows);
    }

    [Fact]
    public async Task Scan_RecountsChangedFile()
    {
        var path = WriteCsv("2024-01_1h.csv", 5);
        var known = new Dictionary<string, MonthPartitionRow>
        {
            ["2024-01"] = new("2024-01", 999, 1, DateTime.UnixEpoch.ToString("O")),
        };

        var rows = await _scanner.Scan(_dir, "1h", known, Ct);
        Assert.Equal(5, Assert.Single(rows).Rows);
    }

    [Fact]
    public async Task Scan_MissingDir_ReturnsEmpty()
    {
        Assert.Empty(await _scanner.Scan(Path.Combine(_dir, "nope"), "1h",
            new Dictionary<string, MonthPartitionRow>(), Ct));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
