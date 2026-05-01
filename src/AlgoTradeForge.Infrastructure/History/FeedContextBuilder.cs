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
            // Phase 2b — analytical sidecars (.flow) are NOT eager-loaded. They cost the same
            // CSV-parse work as any other side feed but are only consumed by strategies that
            // ask for the primary's flow data. Lazy registration happens below if the
            // strategy's primary references this sidecar (TRD §9.4 zero-cost contract).
            if (feedName.EndsWith(".flow", StringComparison.Ordinal))
                continue;

            // Aggregated alt-bar entries are bar feeds, not side feeds — engine reads them via
            // the bar loader path, not via the FeedSeries loader. Skip to avoid trying to parse
            // OHLC longs as side-feed doubles (which would throw on the first row).
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

        // Phase 2b — primary sidecar lazy binding. We expect the manifest pointer (parent's
        // Sidecar field) to reference a `<feedId>.flow` entry in the same feeds.json. If the
        // sidecar entry is missing (broken manifest), surface at engine init via the lazy
        // loader's null return (BacktestFeedContext.EnsurePrimarySidecarMaterialized throws).
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

            // Sidecar files live under `<assetDir>/aggregated/<sidecarFeedId>/<YYYY-MM>.csv`
            // (TRD §3.1, sibling of the parent bar dir). The loader combines its `feedName`
            // arg into the path verbatim, so we prepend the `aggregated/` segment here.
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

        // A registered lazy sidecar is real binding — it counts even though no eager side feed
        // loaded. Without this, an asset whose feeds.json contains only an EqI primary + its
        // .flow sidecar (no funding-rate / OI / etc.) would silently lose its sidecar binding
        // and the strategy would receive a null IFeedContext.
        return (loaded > 0 || sidecarRegistered) ? context : null;
    }
}
