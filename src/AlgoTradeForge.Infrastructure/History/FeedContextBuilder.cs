using System.Text.Json;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Infrastructure.History;

/// <summary>
/// Builds a <see cref="BacktestFeedContext"/> by reading the asset's <c>feeds.json</c>
/// and loading each declared feed from monthly-partitioned CSV files.
/// </summary>
public sealed class FeedContextBuilder(CsvFeedSeriesLoader feedSeriesLoader) : IFeedContextBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public BacktestFeedContext? Build(string dataRoot, Asset asset, DateOnly from, DateOnly to)
    {
        var assetDir = AssetDirectoryName.From(asset);
        var feedsJsonPath = Path.Combine(dataRoot, asset.Exchange, assetDir, "feeds.json");

        if (!File.Exists(feedsJsonPath))
            return null;

        FeedMetadata? metadata;
        try
        {
            using var fs = new FileStream(feedsJsonPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            metadata = JsonSerializer.Deserialize<FeedMetadata>(fs, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (metadata is null || metadata.Feeds.Count == 0)
            return null;

        var context = new BacktestFeedContext();
        var loaded = 0;

        foreach (var (feedName, def) in metadata.Feeds)
        {
            var series = feedSeriesLoader.Load(
                dataRoot, asset.Exchange, assetDir, feedName, def.Interval, from, to);

            if (series is null)
                continue;

            AutoApplyConfig? autoApply = null;
            if (def.AutoApply is not null)
            {
                if (Enum.TryParse<AutoApplyType>(def.AutoApply.Type, ignoreCase: true, out var applyType))
                {
                    autoApply = new AutoApplyConfig(applyType, def.AutoApply.RateColumn, def.AutoApply.SignConvention);
                }
            }

            var schema = new DataFeedSchema(feedName, def.Columns, autoApply);
            context.Register(feedName, schema, series, asset);
            loaded++;
        }

        return loaded > 0 ? context : null;
    }
}
