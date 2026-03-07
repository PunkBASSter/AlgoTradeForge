using System.Net;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Infrastructure.Optimization;
using AlgoTradeForge.Infrastructure.Tests.Live.Testnet;
using AlgoTradeForge.WebApi.Tests.TestUtilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTradeForge.WebApi.Tests.Live.Testnet;

public sealed class TestnetApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private static readonly string TestDataDir =
        Path.Combine(Path.GetTempPath(), "AlgoTradeForge_TestnetE2E");

    private static readonly string EventLogsDir = Path.Combine(TestDataDir, "EventLogs");
    private readonly string _dbPath = Path.Combine(TestDataDir, "test-runs.sqlite");

    internal TestnetE2EStrategy SharedStrategy { get; } = new(new TestnetE2EStrategyParams());

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
        builder.UseSetting("BinanceLive:Accounts:paper:ApiKey", BinanceTestnetCredentials.ApiKey);
        builder.UseSetting("BinanceLive:Accounts:paper:ApiSecret", BinanceTestnetCredentials.ApiSecret);

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter, SetLoopbackIpStartupFilter>();
            services.PostConfigure<EventLogStorageOptions>(o => o.Root = EventLogsDir);
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStrategyFactory>(sp =>
            {
                var descriptorBuilder = sp.GetRequiredService<SpaceDescriptorBuilder>();
                var original = new OptimizationStrategyFactory(descriptorBuilder);
                return new TestnetStrategyFactoryWrapper(original, SharedStrategy);
            });
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

internal sealed class TestnetStrategyFactoryWrapper(
    IStrategyFactory inner,
    TestnetE2EStrategy sharedStrategy) : IStrategyFactory
{
    public IInt64BarStrategy Create(string strategyName, IIndicatorFactory indicatorFactory,
        IDictionary<string, object>? parameters = null)
    {
        if (strategyName == "TestnetE2E")
            return sharedStrategy;

        return inner.Create(strategyName, indicatorFactory, parameters);
    }
}