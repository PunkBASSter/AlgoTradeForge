using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Collection;
using AlgoTradeForge.HistoryLoader.Endpoints;
using AlgoTradeForge.HistoryLoader.Infrastructure;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog(cfg => cfg.ReadFrom.Configuration(builder.Configuration));

builder.Services.Configure<HistoryLoaderOptions>(
    builder.Configuration.GetSection("HistoryLoader"));

builder.Services.AddHealthChecks();

// Infrastructure services (Binance clients, CSV writers, rate limiting, etc.)
builder.Services.AddHistoryLoaderInfrastructure();

// Collection services
builder.Services.AddSingleton<SymbolCollector>();
builder.Services.AddSingleton<BackfillOrchestrator>();
builder.Services.AddHostedService<KlineCollectorService>();
builder.Services.AddHostedService<FundingRateCollectorService>();
builder.Services.AddHostedService<OiCollectorService>();
builder.Services.AddHostedService<RatioCollectorService>();
builder.Services.AddHostedService<HourlyCollectorService>();
builder.Services.AddHostedService<LiquidationCollectorService>();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapStatusEndpoints();
app.MapBackfillEndpoints();

app.Run();
