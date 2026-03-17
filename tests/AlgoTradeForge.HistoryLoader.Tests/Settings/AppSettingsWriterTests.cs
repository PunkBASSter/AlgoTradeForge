using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTradeForge.HistoryLoader.WebApi;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Settings;

public sealed class AppSettingsWriterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly string _filePath;

    public AppSettingsWriterTests()
    {
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "appsettings.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private AppSettingsWriter CreateWriter() =>
        new(_filePath, NullLogger<AppSettingsWriter>.Instance);

    private static string MinimalSettings(
        string symbol = "BTCUSDT",
        string type = "perpetual",
        string feedName = "open-interest",
        string feedInterval = "5m",
        string? historyStart = null)
    {
        var feedNode = new JsonObject
        {
            ["Name"] = feedName,
            ["Interval"] = feedInterval,
        };
        if (historyStart is not null)
            feedNode["HistoryStart"] = historyStart;

        var root = new JsonObject
        {
            ["Serilog"] = new JsonObject { ["MinimumLevel"] = "Information" },
            ["HistoryLoader"] = new JsonObject
            {
                ["MaxBackfillConcurrency"] = 8,
                ["Assets"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["Symbol"] = symbol,
                        ["Type"] = type,
                        ["DecimalDigits"] = 2,
                        ["Feeds"] = new JsonArray { feedNode }
                    }
                }
            }
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // -------------------------------------------------------------------------
    // 1. Writes correct date to matching feed
    // -------------------------------------------------------------------------

    [Fact]
    public void UpdateFeedHistoryStart_WritesCorrectDate()
    {
        File.WriteAllText(_filePath, MinimalSettings());
        var writer = CreateWriter();

        writer.UpdateFeedHistoryStart("BTCUSDT", "perpetual", "open-interest", "5m",
            new DateOnly(2020, 8, 14));

        var updated = JsonNode.Parse(File.ReadAllText(_filePath))!;
        var feed = updated["HistoryLoader"]!["Assets"]![0]!["Feeds"]![0]!;
        Assert.Equal("2020-08-14", feed["HistoryStart"]!.GetValue<string>());
    }

    // -------------------------------------------------------------------------
    // 2. Preserves other JSON properties
    // -------------------------------------------------------------------------

    [Fact]
    public void UpdateFeedHistoryStart_PreservesOtherProperties()
    {
        File.WriteAllText(_filePath, MinimalSettings());
        var writer = CreateWriter();

        writer.UpdateFeedHistoryStart("BTCUSDT", "perpetual", "open-interest", "5m",
            new DateOnly(2020, 8, 14));

        var updated = JsonNode.Parse(File.ReadAllText(_filePath))!;

        // Serilog section preserved
        Assert.Equal("Information",
            updated["Serilog"]!["MinimumLevel"]!.GetValue<string>());

        // MaxBackfillConcurrency preserved
        Assert.Equal(8,
            updated["HistoryLoader"]!["MaxBackfillConcurrency"]!.GetValue<int>());

        // Feed Name/Interval preserved
        var feed = updated["HistoryLoader"]!["Assets"]![0]!["Feeds"]![0]!;
        Assert.Equal("open-interest", feed["Name"]!.GetValue<string>());
        Assert.Equal("5m", feed["Interval"]!.GetValue<string>());
    }

    // -------------------------------------------------------------------------
    // 3. Overwrites existing HistoryStart
    // -------------------------------------------------------------------------

    [Fact]
    public void UpdateFeedHistoryStart_OverwritesExistingDate()
    {
        File.WriteAllText(_filePath, MinimalSettings(historyStart: "2019-01-01"));
        var writer = CreateWriter();

        writer.UpdateFeedHistoryStart("BTCUSDT", "perpetual", "open-interest", "5m",
            new DateOnly(2020, 8, 14));

        var updated = JsonNode.Parse(File.ReadAllText(_filePath))!;
        var feed = updated["HistoryLoader"]!["Assets"]![0]!["Feeds"]![0]!;
        Assert.Equal("2020-08-14", feed["HistoryStart"]!.GetValue<string>());
    }

    // -------------------------------------------------------------------------
    // 4. No match → file unchanged, no crash
    // -------------------------------------------------------------------------

    [Fact]
    public void UpdateFeedHistoryStart_NoMatch_FileUnchanged()
    {
        var original = MinimalSettings();
        File.WriteAllText(_filePath, original);
        var writer = CreateWriter();

        writer.UpdateFeedHistoryStart("ETHUSDT", "spot", "candles", "1d",
            new DateOnly(2021, 1, 1));

        var after = File.ReadAllText(_filePath);
        // File should be identical (no write happened).
        Assert.Equal(original, after);
    }
}
