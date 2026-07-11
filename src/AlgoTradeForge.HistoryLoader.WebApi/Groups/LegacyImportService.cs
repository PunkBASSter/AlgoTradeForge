using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Groups;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Groups;

/// <summary>Runs once at startup: if no group files exist yet and appsettings.Assets is non-empty,
/// converts the legacy asset list to collection-group files. This service WRITES group files —
/// it is the sanctioned exception to "machine never writes groups" because it materializes the
/// user's OWN pre-existing appsettings declaration once.</summary>
internal sealed class LegacyImportService(
    IGroupStore store,
    ArchiveMaterializerRegistry replenishables,
    IOptions<HistoryLoaderOptions> options,
    ILogger<LegacyImportService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var opts = options.Value;
            if (opts.Assets.Count == 0)
                return;

            var existing = await store.List(stoppingToken);
            if (existing.Count > 0)
                return;

            var (groups, warnings) = LegacyGroupImporter.Convert(opts, replenishables);

            foreach (var warning in warnings)
                logger.LogWarning("{Warning}", warning);

            foreach (var group in groups)
            {
                try
                {
                    await store.Put(group.Name, group, expectedETag: null, stoppingToken);
                    logger.LogInformation("legacy-import: created group '{Name}'", group.Name);
                }
                catch (Exception ex) when (!IsTrueShutdown(ex, stoppingToken))
                {
                    logger.LogError(ex, "legacy-import: failed to write group '{Name}'", group.Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "legacy-import: unexpected error during startup import");
        }
    }

    private static bool IsTrueShutdown(Exception ex, CancellationToken stoppingToken) =>
        ex is OperationCanceledException && stoppingToken.IsCancellationRequested;
}
