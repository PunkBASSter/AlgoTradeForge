using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;
using AlgoTradeForge.HistoryLoader.Collection;
using AlgoTradeForge.HistoryLoader.Endpoints;
using AlgoTradeForge.HistoryLoader.Infrastructure;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog(cfg => cfg.ReadFrom.Configuration(builder.Configuration));

builder.Services.Configure<HistoryLoaderOptions>(
    builder.Configuration.GetSection("HistoryLoader"));
builder.Services.AddSingleton<IValidateOptions<HistoryLoaderOptions>, HistoryLoaderOptionsValidator>();

builder.Services.AddHealthChecks();

// Infrastructure services (Binance clients, CSV writers, rate limiting, etc.)
builder.Services.AddHistoryLoaderInfrastructure();

// Feed collectors
builder.Services.AddSingleton<IFeedCollector, CandleFeedCollector>();
builder.Services.AddSingleton<IFeedCollector, FundingRateFeedCollector>();
builder.Services.AddSingleton<IFeedCollector, MarkPriceFeedCollector>();
builder.Services.AddSingleton<IFeedCollector, OpenInterestFeedCollector>();
builder.Services.AddSingleton<IFeedCollector, LsRatioGlobalFeedCollector>();
builder.Services.AddSingleton<IFeedCollector, LsRatioTopAccountsFeedCollector>();
builder.Services.AddSingleton<IFeedCollector, TakerVolumeFeedCollector>();
builder.Services.AddSingleton<IFeedCollector, LsRatioTopPositionsFeedCollector>();
builder.Services.AddSingleton<IFeedCollector, LiquidationFeedCollector>();

// Collection services
builder.Services.AddSingleton<ICollectionCircuitBreaker, CollectionCircuitBreaker>();
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
