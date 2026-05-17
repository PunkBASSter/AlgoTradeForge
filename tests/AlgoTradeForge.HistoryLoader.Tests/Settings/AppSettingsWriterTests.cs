using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTradeForge.HistoryLoader.WebApi;
using AlgoTradeForge.Infrastructure.IO;
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AppSettingsWriter CreateWriter() =>
        new(_filePath, new LocalFileStorage(), NullLogger<AppSettingsWriter>.Instance);

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

    [Fact]
    public async Task UpdateFeedHistoryStart_WritesCorrectDate()
    {
        File.WriteAllText(_filePath, MinimalSettings());
        var writer = CreateWriter();

        await writer.UpdateFeedHistoryStart("BTCUSDT", "perpetual", "open-interest", "5m",
            new DateOnly(2020, 8, 14), Ct);

        var updated = JsonNode.Parse(File.ReadAllText(_filePath))!;
        var feed = updated["HistoryLoader"]!["Assets"]![0]!["Feeds"]![0]!;
        Assert.Equal("2020-08-14", feed["HistoryStart"]!.GetValue<string>());
    }

    [Fact]
    public async Task UpdateFeedHistoryStart_PreservesOtherProperties()
    {
        File.WriteAllText(_filePath, MinimalSettings());
        var writer = CreateWriter();

        await writer.UpdateFeedHistoryStart("BTCUSDT", "perpetual", "open-interest", "5m",
            new DateOnly(2020, 8, 14), Ct);

        var updated = JsonNode.Parse(File.ReadAllText(_filePath))!;

        Assert.Equal("Information",
            updated["Serilog"]!["MinimumLevel"]!.GetValue<string>());

        Assert.Equal(8,
            updated["HistoryLoader"]!["MaxBackfillConcurrency"]!.GetValue<int>());

        var feed = updated["HistoryLoader"]!["Assets"]![0]!["Feeds"]![0]!;
        Assert.Equal("open-interest", feed["Name"]!.GetValue<string>());
        Assert.Equal("5m", feed["Interval"]!.GetValue<string>());
    }

    [Fact]
    public async Task UpdateFeedHistoryStart_OverwritesExistingDate()
    {
        File.WriteAllText(_filePath, MinimalSettings(historyStart: "2019-01-01"));
        var writer = CreateWriter();

        await writer.UpdateFeedHistoryStart("BTCUSDT", "perpetual", "open-interest", "5m",
            new DateOnly(2020, 8, 14), Ct);

        var updated = JsonNode.Parse(File.ReadAllText(_filePath))!;
        var feed = updated["HistoryLoader"]!["Assets"]![0]!["Feeds"]![0]!;
        Assert.Equal("2020-08-14", feed["HistoryStart"]!.GetValue<string>());
    }

    [Fact]
    public async Task UpdateFeedHistoryStart_NoMatch_FileUnchanged()
    {
        var original = MinimalSettings();
        File.WriteAllText(_filePath, original);
        var writer = CreateWriter();

        await writer.UpdateFeedHistoryStart("ETHUSDT", "spot", "candles", "1d",
            new DateOnly(2021, 1, 1), Ct);

        var after = File.ReadAllText(_filePath);
        Assert.Equal(original, after);
    }
}
