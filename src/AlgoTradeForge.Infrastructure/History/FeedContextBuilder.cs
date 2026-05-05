using System.Text.Json;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.Infrastructure.History;

/// <summary>
/// Builds a <see cref="BacktestFeedContext"/> by reading the asset's <c>feeds.json</c>
/// and loading each declared feed from monthly-partitioned CSV files.
/// </summary>
public sealed class FeedContextBuilder(
    IFeedSeriesLoader feedSeriesLoader,
    ILogger<FeedContextBuilder> logger) : IFeedContextBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public BacktestFeedContext? Build(
        string dataRoot,
        Asset asset,
        DateOnly from,
        DateOnly to,
        string? primaryFeedName = null)
    {
        var assetDir = AssetDirectoryName.From(asset);
        var feedsJsonPath = Path.Combine(dataRoot, asset.Exchange, assetDir, "feeds.json");

        if (!File.Exists(feedsJsonPath))
        {
            logger.LogDebug("No feeds.json found at {Path} for {Asset}", feedsJsonPath, asset.Name);
            return null;
        }

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
        var sidecarRegistered = false;

        foreach (var (feedName, def) in metadata.Feeds)
        {
            // .flow sidecars are lazy-loaded below only if a strategy's primary references them.
            if (feedName.EndsWith(".flow", StringComparison.Ordinal))
                continue;

            // OHLCV/Tick entries are bar feeds, read via the bar loader path — not as side feeds.
            if (string.Equals(def.Kind, "OHLCV_AltBar", StringComparison.Ordinal) ||
                string.Equals(def.Kind, "aggregated", StringComparison.Ordinal) ||
                string.Equals(def.Kind, "OHLCV_TimeBar", StringComparison.Ordinal) ||
                string.Equals(def.Kind, "Tick", StringComparison.Ordinal))
                continue;

            var series = feedSeriesLoader.Load(
                dataRoot, asset.Exchange, assetDir, feedName, def.Interval ?? string.Empty, from, to,
                nullableColumns: def.NullableColumns ?? false);

            if (series is null)
                continue;

            AutoApplyConfig? autoApply = null;
            if (def.AutoApply is not null)
            {
                if (Enum.TryParse<AutoApplyType>(def.AutoApply.Type, ignoreCase: true, out var applyType))
                {
                    autoApply = new AutoApplyConfig(applyType, def.AutoApply.RateColumn, def.AutoApply.SignConvention);
                }
                else
                {
                    logger.LogWarning(
                        "Invalid AutoApplyType '{Type}' for feed '{Feed}' in {Asset} — auto-apply will be disabled for this feed",
                        def.AutoApply.Type, feedName, asset.Name);
                }
            }

            var schema = new DataFeedSchema(feedName, def.Columns, autoApply);
            context.Register(feedName, schema, series, asset);
            loaded++;
        }

        // Primary's sidecar (.flow) is registered lazily — only materialized when the strategy reads it.
        if (primaryFeedName is not null
            && metadata.Feeds.TryGetValue(primaryFeedName, out var primaryDef)
            && !string.IsNullOrEmpty(primaryDef.Sidecar))
        {
            var sidecarFeedId = primaryDef.Sidecar;
            if (!metadata.Feeds.TryGetValue(sidecarFeedId, out var sidecarDef))
            {
                throw new InvalidOperationException(
                    $"Primary feed '{primaryFeedName}' references sidecar '{sidecarFeedId}' but that " +
                    $"sidecar entry is missing from feeds.json for asset {asset.Name}. The manifest " +
                    "must list both atomically (see ISchemaManager.EnsureAltBarWithSidecar).");
            }

            // Sidecar files live under <assetDir>/aggregated/<sidecarFeedId>/. The loader
            // appends feedName to the path verbatim, so the "aggregated/" segment goes here.
            var sidecarFeedNameOnDisk = Path.Combine("aggregated", sidecarFeedId);
            var sidecarSchema = new DataFeedSchema(sidecarFeedId, sidecarDef.Columns, AutoApply: null);
            context.RegisterPrimarySidecarLazy(
                sidecarFeedId,
                sidecarSchema,
                () => feedSeriesLoader.Load(
                    dataRoot, asset.Exchange, assetDir,
                    feedName: sidecarFeedNameOnDisk,
                    interval: sidecarDef.Interval ?? string.Empty,
                    from, to,
                    nullableColumns: sidecarDef.NullableColumns ?? true));
            sidecarRegistered = true;
        }

        // A registered lazy sidecar counts as a binding — without this, an asset with only a
        // primary + .flow sidecar (no other side feeds) would return a null IFeedContext.
        return (loaded > 0 || sidecarRegistered) ? context : null;
    }
}
