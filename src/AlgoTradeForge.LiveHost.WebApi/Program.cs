using System.Reflection;
using AlgoTradeForge.Application;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Infrastructure;
using AlgoTradeForge.Infrastructure.History;
using AlgoTradeForge.Infrastructure.Plugins;
using AlgoTradeForge.LiveHost.Infrastructure.Live;
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using AlgoTradeForge.LiveHost.WebApi;
using AlgoTradeForge.LiveHost.WebApi.Endpoints;
using AlgoTradeForge.Storage;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();
builder.Host.UseSystemd();

var logDir = Path.Combine(builder.Environment.ContentRootPath, "logs");
builder.Services.AddSerilog(cfg => cfg
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.File(Path.Combine(logDir, "livehost-.log"),
        rollingInterval: Serilog.RollingInterval.Day, shared: true));

builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "AlgoTradeForge LiveHost API",
        Version = "v1",
        Description = "API for live and paper trading session management"
    });
});

// Domain services required by live handlers (and shared Application layer)
builder.Services.AddSingleton<IBarMatcher, BarMatcher>();
builder.Services.AddSingleton<IOrderValidator, OrderValidator>();
builder.Services.AddSingleton<IMetricsCalculator, MetricsCalculator>();
builder.Services.AddSingleton<BacktestEngine>();

builder.Services.AddDistributedMemoryCache();

// Application layer (registers IEventBus, progress cache, run timeout defaults, etc.)
builder.Services.AddApplication();

builder.Services.Configure<RunTimeoutOptions>(
    builder.Configuration.GetSection("RunTimeouts"));

builder.Services.Configure<RunStorageOptions>(
    builder.Configuration.GetSection("RunStorage"));

// Storage backend
builder.Services.Configure<StorageOptions>(
    builder.Configuration.GetSection("Storage"));
foreach (var warning in StorageConfigMigration.ApplyLegacyAliases(builder.Configuration, builder.Services))
    Console.Error.WriteLine($"[Storage] {warning}");

// Infrastructure services for data access
builder.Services.Configure<CandleStorageOptions>(
    builder.Configuration.GetSection("CandleStorage"));
builder.Services.AddSingleton<IInt64BarLoader, PartitionedCsvBarLoader>();
builder.Services.AddSingleton<IFeedSeriesLoader, CsvFeedSeriesLoader>();
builder.Services.AddSingleton<IFeedContextBuilder, FeedContextBuilder>();
builder.Services.AddSingleton<IAvailableAssetsProvider, FileSystemAvailableAssetsProvider>();
builder.Services.AddSingleton<IDataSource, CsvDataSource>();
builder.Services.AddSingleton<IHistoryRepository, HistoryRepository>();
builder.Services.AddSingleton<IAssetRepository, FileSystemAssetRepository>();

// Load plugin assemblies (needed for strategy discovery)
var pluginPaths = builder.Configuration.GetSection("Plugins:Paths").Get<string[]>() ?? ["plugins"];
using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var pluginLogger = loggerFactory.CreateLogger("PluginLoader");
var pluginAssemblies = PluginLoader.LoadFrom(pluginPaths, pluginLogger, builder.Environment.ContentRootPath);

foreach (var asm in pluginAssemblies)
{
    foreach (var type in asm.GetTypes().Where(t => !t.IsAbstract && typeof(IPluginInitializer).IsAssignableFrom(t)))
    {
        var initializer = (IPluginInitializer)Activator.CreateInstance(type)!;
        initializer.ConfigureServices(builder.Services);
    }
}

// Infrastructure (registers IStrategyFactory, IOptimizationSpaceProvider, IFileStorage, run repos, etc.)
Assembly[] strategyAssemblies = [typeof(AlgoTradeForge.Domain.Strategy.StrategyBase<>).Assembly, .. pluginAssemblies];
builder.Services.AddInfrastructure(strategyAssemblies);

// Catch-up / recovery options: RelayKeyPrefix MUST equal RelayPumpOptions.KeyPrefix (no venue suffix).
// RelayArchiveReplaySource appends "/{venue}" when listing, matching the relay's upload path
// "{KeyPrefix}/{venue}/{instrument}/trades/{file}".
builder.Services.Configure<AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery.CatchupOptions>(opts =>
{
    builder.Configuration.GetSection("Catchup").Bind(opts);
    // Ensure relay key prefix matches the relay pump's upload prefix.
    if (string.IsNullOrEmpty(opts.RelayKeyPrefix))
        opts.RelayKeyPrefix = builder.Configuration.GetValue<string>("RelayPump:KeyPrefix") ?? "live-md";
    if (string.IsNullOrEmpty(opts.DataRoot))
        opts.DataRoot = builder.Configuration.GetValue<string>("CandleStorage:DataRoot") ?? "data";
});
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery.CatchupOptions>>().Value);
builder.Services.AddSingleton<AlgoTradeForge.LiveHost.Application.Live.Recovery.IReplaySource>(sp =>
    new AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery.RelayArchiveReplaySource(
        sp.GetRequiredService<AlgoTradeForge.Storage.IFileStorage>(),
        sp.GetRequiredService<AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery.CatchupOptions>().RelayKeyPrefix));
// Data plane: venue-specific registrations (IVenueConnector, IBarSourceResolver, IBackfillRequester).
// Venue-agnostic services (ITickRouter, IStrategyDispatch, relay pump) are registered outside this branch.
var venue = VenueSelector.Parse(builder.Configuration.GetValue<string>("Venue"));

if (venue == VenueKind.Ib)
{
    builder.Services.AddIbDataPlane(builder.Configuration);
}
else
{
    // Binance data plane
    builder.Services.AddSingleton<AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery.IAggTradeBackfillClient,
        AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery.NullAggTradeBackfillClient>();
    builder.Services.AddSingleton<AlgoTradeForge.LiveHost.Application.Live.Recovery.IBackfillRequester,
        AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery.BinanceBackfillRequester>();
    builder.Services.AddSingleton(sp =>
    {
        var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AlgoTradeForge.LiveHost.Infrastructure.Live.Binance.BinanceLiveOptions>>().Value;
        var log = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AlgoTradeForge.LiveHost.Infrastructure.Live.Binance.BinanceWebSocketManager>>();
        return new AlgoTradeForge.LiveHost.Infrastructure.Live.Binance.BinanceWebSocketManager(
            opts.MarketStreamUrl, opts.ReconnectDelay, opts.MaxReconnectAttempts, log);
    });
    builder.Services.AddSingleton<AlgoTradeForge.LiveHost.Application.Live.DataPlane.IBarSourceResolver>(sp =>
        new AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane.BarSourceResolver(
            sp.GetRequiredService<AlgoTradeForge.LiveHost.Infrastructure.Live.Binance.BinanceWebSocketManager>(),
            sp.GetRequiredService<AlgoTradeForge.LiveHost.Application.Live.Recovery.IReplaySource>(),
            sp.GetRequiredService<AlgoTradeForge.LiveHost.Application.Live.Recovery.IBackfillRequester>(),
            sp.GetRequiredService<AlgoTradeForge.Application.CandleIngestion.IInt64BarLoader>(),
            sp.GetRequiredService<AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery.CatchupOptions>()));
    builder.Services.AddSingleton<AlgoTradeForge.Live.Relay.IVenueConnector>(sp =>
    {
        var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AlgoTradeForge.LiveHost.Infrastructure.Live.Binance.BinanceLiveOptions>>().Value;
        var log = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AlgoTradeForge.LiveHost.Infrastructure.Live.Binance.BinanceVenueConnector>>();
        return new AlgoTradeForge.LiveHost.Infrastructure.Live.Binance.BinanceVenueConnector(opts, log);
    });
}

// Venue-agnostic data-plane services (shared by all venues)
builder.Services.AddSingleton<AlgoTradeForge.LiveHost.Application.Live.DataPlane.IStrategyDispatch,
    AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane.StrategyDispatch>();
builder.Services.AddSingleton<AlgoTradeForge.LiveHost.Application.Live.DataPlane.ITickRouter,
    AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane.TickRouter>();
builder.Services.AddSingleton<AlgoTradeForge.Live.Relay.IRelayTradeTap,
    AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane.TickRouterTradeTap>();

// Live host services (BinanceLiveOptions, ILiveSessionStore, handlers, ILiveAccountManager, etc.)
builder.Services.AddLiveHost(builder.Configuration);

// Relay pump (ingest → local segment files → IFileStorage archival)
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<RelayPumpOptions>(builder.Configuration.GetSection("RelayPump"));
builder.Services.AddHostedService<RelayPumpHostedService>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:3000")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "LiveHost API v1");
    });
}
else
{
    app.UseHttpsRedirection();
}

app.UseCors();

app.MapGet("/health", () => Results.Ok("livehost"));
app.MapLiveEndpoints();
app.MapConfigEndpoints();

app.Run();

public partial class Program { }
