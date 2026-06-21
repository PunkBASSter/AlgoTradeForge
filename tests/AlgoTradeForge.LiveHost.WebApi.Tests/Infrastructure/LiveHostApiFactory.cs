using System.Net;
using AlgoTradeForge.Application.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTradeForge.LiveHost.WebApi.Tests.Infrastructure;

public sealed class LiveHostApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private static readonly string TestDataDir =
        Path.Combine(Path.GetTempPath(), "AlgoTradeForge_LiveHostTests");

    private static readonly string EventLogsDir = Path.Combine(TestDataDir, "EventLogs");
    private readonly string _dbPath = Path.Combine(TestDataDir, "test-runs.sqlite");

    public ValueTask InitializeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(TestDataDir))
        {
            try { Directory.Delete(TestDataDir, recursive: true); }
            catch { /* best-effort */ }
        }
        Directory.CreateDirectory(TestDataDir);
        Directory.CreateDirectory(EventLogsDir);
        return ValueTask.CompletedTask;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var testDataRoot = Path.Combine(AppContext.BaseDirectory, "TestData", "Candles");

        builder.UseEnvironment("Development");
        builder.UseSetting("CandleStorage:DataRoot", testDataRoot);
        builder.UseSetting("RunStorage:DatabasePath", _dbPath);

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter, SetLoopbackIpStartupFilter>();
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

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }
}
