using System.Reflection;
using AlgoTradeForge.Application;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Storage;
using AlgoTradeForge.Application.Live;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Application.Validation;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Infrastructure;
using AlgoTradeForge.WebApi;
using AlgoTradeForge.Infrastructure.History;
using AlgoTradeForge.Infrastructure.Live.Binance;
using AlgoTradeForge.Infrastructure.Plugins;
using System.Text;
using AlgoTradeForge.WebApi.Data;
using AlgoTradeForge.WebApi.Endpoints;
using AlgoTradeForge.WebApi.Middleware;

// ── Diagnostic file logging (set DIAG_LOG_FILE env var to enable) ──
StreamWriter? diagWriter = null;
var diagLogPath = Environment.GetEnvironmentVariable("DIAG_LOG_FILE");
if (diagLogPath is not null)
{
    diagWriter = new StreamWriter(diagLogPath, append: false) { AutoFlush = true };
    var originalOut = Console.Out;
    var originalErr = Console.Error;
    Console.SetOut(new TeeTextWriter(originalOut, diagWriter));
    Console.SetError(new TeeTextWriter(originalErr, diagWriter));
}

var builder = WebApplication.CreateBuilder(args);

// Shared JSON options for FE-facing API (camelCase + case-insensitive)
builder.Services.AddSingleton(JsonDefaults.Api);

// Single source of truth for wire JSON policy (camelCase, NaN/Infinity round-trip, string enums).
builder.Services.ConfigureHttpJsonOptions(options => JsonDefaults.Apply(options.SerializerOptions));

// Add OpenAPI/Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "AlgoTradeForge API",
        Version = "v1",
        Description = "API for algorithmic trading backtesting"
    });
});

// Register Domain services
builder.Services.AddSingleton<IBarMatcher, BarMatcher>();
builder.Services.AddSingleton<IOrderValidator, OrderValidator>();
builder.Services.AddSingleton<IMetricsCalculator, MetricsCalculator>();
builder.Services.AddSingleton<BacktestEngine>();

// Register distributed cache (in-memory; swappable to Redis via DI)
builder.Services.AddDistributedMemoryCache();

// Register Application services
builder.Services.AddApplication();

// Register run timeout config
builder.Services.Configure<RunTimeoutOptions>(
    builder.Configuration.GetSection("RunTimeouts"));

// Register run persistence config
builder.Services.Configure<RunStorageOptions>(
    builder.Configuration.GetSection("RunStorage"));

// Register live trading config
builder.Services.Configure<BinanceLiveOptions>(
    builder.Configuration.GetSection("BinanceLive"));

// Register simulation cache config
builder.Services.Configure<SimulationCacheOptions>(
    builder.Configuration.GetSection("SimulationCache"));

// Storage backend (LocalFileSystem | S3). Bound here so AddInfrastructure's IFileStorage /
// IPartitionTailIndex factories see it. S3 settings (Bucket, KeyPrefix, …) live under Storage:S3.
builder.Services.Configure<StorageOptions>(
    builder.Configuration.GetSection("Storage"));
foreach (var warning in StorageConfigMigration.ApplyLegacyAliases(builder.Configuration, builder.Services))
    Console.Error.WriteLine($"[Storage] {warning}");

// Register Infrastructure services
builder.Services.Configure<CandleStorageOptions>(
    builder.Configuration.GetSection("CandleStorage"));
builder.Services.AddSingleton<IInt64BarLoader, PartitionedCsvBarLoader>();
builder.Services.AddSingleton<IFeedSeriesLoader, CsvFeedSeriesLoader>();
builder.Services.AddSingleton<IFeedContextBuilder, FeedContextBuilder>();
builder.Services.AddSingleton<IAvailableAssetsProvider, FileSystemAvailableAssetsProvider>();
builder.Services.AddSingleton<IDataSource, CsvDataSource>();
builder.Services.AddSingleton<IHistoryRepository, HistoryRepository>();

// Load plugin assemblies
var pluginPaths = builder.Configuration.GetSection("Plugins:Paths").Get<string[]>() ?? ["plugins"];
using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var pluginLogger = loggerFactory.CreateLogger("PluginLoader");
var pluginAssemblies = PluginLoader.LoadFrom(pluginPaths, pluginLogger, builder.Environment.ContentRootPath);

// Invoke plugin initializers
foreach (var asm in pluginAssemblies)
{
    foreach (var type in asm.GetTypes().Where(t => !t.IsAbstract && typeof(IPluginInitializer).IsAssignableFrom(t)))
    {
        var initializer = (IPluginInitializer)Activator.CreateInstance(type)!;
        initializer.ConfigureServices(builder.Services);
    }
}

// Register optimization infrastructure (domain + plugin assemblies)
Assembly[] strategyAssemblies = [typeof(AlgoTradeForge.Domain.Strategy.StrategyBase<>).Assembly, .. pluginAssemblies];
builder.Services.AddInfrastructure(strategyAssemblies);
builder.Services.AddHostedService<SqliteIndexMaintenanceService>();
builder.Services.AddHostedService<ComputeQueueConsumer>();

builder.Services.AddSingleton<IAssetRepository, FileSystemAssetRepository>();

// Debug WebSocket handler (instance class for constructor-injected JSON options)
builder.Services.AddSingleton<DebugWebSocketHandler>();

// History-loader proxy: typed HttpClient over the sibling WebApi.
builder.Services.AddHistoryLoaderClient(builder.Configuration);
builder.Services.AddSingleton<DataProxyCache>();

// CORS for frontend dev server
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

// Silently handle client-disconnect cancellations before DeveloperExceptionPage treats them as errors
app.UseMiddleware<ClientDisconnectMiddleware>();

// Configure HTTP pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "AlgoTradeForge API v1");
    });
}
else
{
    app.UseHttpsRedirection();
}

app.UseCors();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

// Map endpoints
app.MapBacktestEndpoints();
app.MapOptimizationEndpoints();
app.MapStrategyEndpoints();
app.MapDebugEndpoints();
DebugWebSocketHandler.MapDebugWebSocket(app);
app.MapValidationEndpoints();
app.MapTaskQueueEndpoints();
app.MapThresholdProfileEndpoints();
app.MapLiveEndpoints();
app.MapDataEndpoints();

app.Run();

public partial class Program { }

/// <summary>Writes to two TextWriters simultaneously. Used for diagnostic file logging.</summary>
file sealed class TeeTextWriter(TextWriter primary, TextWriter secondary) : TextWriter
{
    public override Encoding Encoding => primary.Encoding;
    public override void Write(char value) { primary.Write(value); secondary.Write(value); }
    public override void Write(string? value) { primary.Write(value); secondary.Write(value); }
    public override void WriteLine(string? value) { primary.WriteLine(value); secondary.WriteLine(value); }
    public override void Flush() { primary.Flush(); secondary.Flush(); }
}
