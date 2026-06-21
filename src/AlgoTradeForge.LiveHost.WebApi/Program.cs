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

// Live host services (BinanceLiveOptions, ILiveSessionStore, handlers, ILiveAccountManager, etc.)
builder.Services.AddLiveHost(builder.Configuration);

// Relay pump (ingest → local segment files → IFileStorage archival)
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<RelayPumpOptions>(builder.Configuration.GetSection("RelayPump"));
builder.Services.AddSingleton<AlgoTradeForge.Live.Relay.IVenueConnector>(sp =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AlgoTradeForge.LiveHost.Infrastructure.Live.Binance.BinanceLiveOptions>>().Value;
    var log = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AlgoTradeForge.LiveHost.Infrastructure.Live.Binance.BinanceVenueConnector>>();
    return new AlgoTradeForge.LiveHost.Infrastructure.Live.Binance.BinanceVenueConnector(opts, log);
});
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

app.Run();

public partial class Program { }
