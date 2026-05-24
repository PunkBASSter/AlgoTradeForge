using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTradeForge.Storage;

/// <summary>
/// Bridges legacy <c>HistoryLoader:DataRoot</c> / <c>CandleStorage:DataRoot</c> keys to the
/// unified <c>Storage:Local:DataRoot</c>. Slated for removal one release after PR 5 — the
/// legacy keys remain functional, but a warning is emitted at startup.
/// </summary>
public static class StorageConfigMigration
{
    /// <summary>Returns the warning lines that callers should surface to stderr / bootstrap logger.</summary>
    public static IReadOnlyList<string> ApplyLegacyAliases(IConfiguration configuration, IServiceCollection services)
    {
        var legacyHistory = configuration["HistoryLoader:DataRoot"];
        var legacyCandle = configuration["CandleStorage:DataRoot"];
        var newLocal = configuration["Storage:Local:DataRoot"];

        if (string.IsNullOrEmpty(legacyHistory) && string.IsNullOrEmpty(legacyCandle))
            return Array.Empty<string>();

        var warnings = new List<string>();
        if (!string.IsNullOrEmpty(legacyHistory))
            warnings.Add("HistoryLoader:DataRoot is deprecated; migrate to Storage:Local:DataRoot.");
        if (!string.IsNullOrEmpty(legacyCandle))
            warnings.Add("CandleStorage:DataRoot is deprecated; migrate to Storage:Local:DataRoot.");

        if (string.IsNullOrEmpty(newLocal))
        {
            var migrated = !string.IsNullOrEmpty(legacyHistory) ? legacyHistory : legacyCandle;
            services.PostConfigure<StorageOptions>(opts =>
            {
                if (string.IsNullOrEmpty(opts.Local.DataRoot)) opts.Local.DataRoot = migrated!;
            });
        }
        else
        {
            warnings.Add("Storage:Local:DataRoot is set; legacy *:DataRoot values are ignored for IFileStorage routing.");
        }

        return warnings;
    }
}
