using System.Reflection;
using AlgoTradeForge.Application;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Application.Live;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Infrastructure;
using AlgoTradeForge.Infrastructure.CandleIngestion;
using AlgoTradeForge.Infrastructure.History;
using AlgoTradeForge.Infrastructure.Live.Binance;
using AlgoTradeForge.Infrastructure.Plugins;
using AlgoTradeForge.Infrastructure.Repositories;
using AlgoTradeForge.WebApi.Endpoints;

var builder = WebApplication.CreateBuilder(args);

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

// Register Infrastructure services
builder.Services.Configure<CandleStorageOptions>(
    builder.Configuration.GetSection("CandleStorage"));
builder.Services.AddSingleton<IInt64BarLoader, CsvInt64BarLoader>();
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

builder.Services.AddSingleton<IAssetRepository, InMemoryAssetRepository>();

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
app.MapDebugWebSocket();
app.MapLiveEndpoints();

app.Run();

public partial class Program { }
