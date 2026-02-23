using System.Net;
using AlgoTradeForge.Application.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTradeForge.WebApi.Tests.Infrastructure;

public sealed class AlgoTradeForgeApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private static readonly string TestDataDir =
        Path.Combine(Path.GetTempPath(), "AlgoTradeForge_ApiTests");

    private static readonly string EventLogsDir = Path.Combine(TestDataDir, "EventLogs");
    private readonly string _dbPath = Path.Combine(TestDataDir, "test-runs.sqlite");

    public Task InitializeAsync()
    {
        // Delete old data from previous runs (best-effort)
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(TestDataDir))
        {
            try { Directory.Delete(TestDataDir, recursive: true); }
            catch { /* best-effort — may be locked by another process */ }
        }

        Directory.CreateDirectory(TestDataDir);
        Directory.CreateDirectory(EventLogsDir);
        return Task.CompletedTask;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var testDataRoot = Path.Combine(AppContext.BaseDirectory, "TestData", "Candles");

        builder.UseEnvironment("Development");

        builder.UseSetting("CandleStorage:DataRoot", testDataRoot);
        builder.UseSetting("RunStorage:DatabasePath", _dbPath);

        builder.ConfigureServices(services =>
        {
            // Set loopback IP so LocalhostOnlyFilter allows debug endpoints
            services.AddSingleton<IStartupFilter, SetLoopbackIpStartupFilter>();

            // Redirect event logs to the test data directory
            services.PostConfigure<EventLogStorageOptions>(o => o.Root = EventLogsDir);
        });
    }

    private sealed class SetLoopbackIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress ??= IPAddress.Loopback;
                await nextMiddleware();
            });
            next(app);
        };
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        // Keep test data after run for investigation — it will be cleaned up on next run
    }
}
