using AlgoTradeForge.Application.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.WebApi;

/// <summary>
/// Creates heavy indexes in the background after app startup so they don't block
/// request serving. Uses <c>IF NOT EXISTS</c> so it's safe to run repeatedly.
/// </summary>
internal sealed class SqliteIndexMaintenanceService(
    IOptions<RunStorageOptions> options,
    ILogger<SqliteIndexMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app start serving requests first
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        var connectionString = $"Data Source={options.Value.DatabasePath}";
        try
        {
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync(stoppingToken);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS ix_br_opt_fitness ON backtest_runs(optimization_run_id, fitness_score DESC)";
            logger.LogInformation("Creating ix_br_opt_fitness index (may take a while on large databases)...");
            await cmd.ExecuteNonQueryAsync(stoppingToken);
            logger.LogInformation("Index ix_br_opt_fitness ready");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Background index creation failed — queries will still work, just slower");
        }
    }
}
