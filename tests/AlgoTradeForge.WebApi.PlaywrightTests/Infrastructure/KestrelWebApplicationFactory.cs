using System.Net;
using AlgoTradeForge.Application.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AlgoTradeForge.WebApi.PlaywrightTests.Infrastructure;

public sealed class KestrelWebApplicationFactory : WebApplicationFactory<Program>
{
    private static readonly string TestDataDir =
        Path.Combine(Path.GetTempPath(), "AlgoTradeForge_E2ETests");

    private static readonly string EventLogsDir = Path.Combine(TestDataDir, "EventLogs");
    private readonly string _dbPath = Path.Combine(TestDataDir, "test-runs.sqlite");

    private IHost? _kestrelHost;

    public KestrelWebApplicationFactory(int backendPort, int frontendPort)
    {
        BackendPort = backendPort;
        FrontendPort = frontendPort;
        BaseUrl = $"http://localhost:{backendPort}";
    }

    public int BackendPort { get; }
    public int FrontendPort { get; }
    public string BaseUrl { get; }

    public void EnsureStarted()
    {
        // Accessing Services triggers host creation & start via CreateHost
        _ = Services;
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

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Clean up from previous runs
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(TestDataDir))
        {
            try { Directory.Delete(TestDataDir, recursive: true); }
            catch { /* best-effort */ }
        }
        Directory.CreateDirectory(TestDataDir);
        Directory.CreateDirectory(EventLogsDir);

        // Add Kestrel on a real port (overrides the TestServer IServer registration)
        builder.ConfigureWebHost(webBuilder =>
        {
            webBuilder.UseKestrel();
            webBuilder.UseUrls(BaseUrl);

            // Override CORS to allow the test frontend port
            webBuilder.ConfigureServices(services =>
            {
                services.AddCors(options =>
                {
                    options.AddDefaultPolicy(policy =>
                    {
                        policy.WithOrigins("http://localhost:3000", $"http://localhost:{FrontendPort}")
                              .AllowAnyHeader()
                              .AllowAnyMethod()
                              .AllowCredentials();
                    });
                });
            });
        });

        // Build and start the real Kestrel host
        _kestrelHost = builder.Build();
        _kestrelHost.Start();

        // Return a dummy host with TestServer so the base class's
        // (TestServer) cast in EnsureServer() succeeds.
        return new HostBuilder()
            .ConfigureWebHost(wb =>
            {
                wb.UseTestServer();
                wb.Configure(_ => { });
            })
            .Build();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _kestrelHost?.StopAsync().GetAwaiter().GetResult();
            _kestrelHost?.Dispose();
        }
        base.Dispose(disposing);
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
}
