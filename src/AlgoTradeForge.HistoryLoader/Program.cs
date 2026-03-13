using AlgoTradeForge.HistoryLoader;
using AlgoTradeForge.HistoryLoader.Binance;
using AlgoTradeForge.HistoryLoader.Collection;
using AlgoTradeForge.HistoryLoader.Endpoints;
using AlgoTradeForge.HistoryLoader.RateLimiting;
using AlgoTradeForge.HistoryLoader.Storage;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog(cfg => cfg.ReadFrom.Configuration(builder.Configuration));

builder.Services.Configure<HistoryLoaderOptions>(
    builder.Configuration.GetSection("HistoryLoader"));

builder.Services.AddHealthChecks();

// Rate limiting — global limiter shared by all sources
builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HistoryLoaderOptions>>().Value;
    return new WeightedRateLimiter(opts.Binance.MaxWeightPerMinute, opts.Binance.WeightBudgetPercent);
});

// Futures source rate limiter
builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HistoryLoaderOptions>>().Value;
    var global = sp.GetRequiredService<WeightedRateLimiter>();
    return new SourceRateLimiter(global, opts.Binance.FuturesBaseUrl);
});

// Binance API clients
builder.Services.AddHttpClient<BinanceFuturesClient>();
builder.Services.AddSingleton(sp =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpFactory.CreateClient(nameof(BinanceFuturesClient));
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HistoryLoaderOptions>>().Value;
    var rateLimiter = sp.GetRequiredService<SourceRateLimiter>();
    return new BinanceFuturesClient(httpClient, opts.Binance, rateLimiter);
});

// Spot source rate limiter (separate instance scoped to spot base URL)
builder.Services.AddHttpClient<BinanceSpotClient>();
builder.Services.AddSingleton(sp =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpFactory.CreateClient(nameof(BinanceSpotClient));
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HistoryLoaderOptions>>().Value;
    var global = sp.GetRequiredService<WeightedRateLimiter>();
    var spotLimiter = new SourceRateLimiter(global, opts.Binance.SpotBaseUrl);
    return new BinanceSpotClient(httpClient, opts.Binance, spotLimiter);
});

// Storage writers
builder.Services.AddSingleton<CandleCsvWriter>();
builder.Services.AddSingleton<FeedCsvWriter>();
builder.Services.AddSingleton<FeedSchemaManager>();

// Collection services
builder.Services.AddSingleton<SymbolCollector>(sp => new SymbolCollector(
    sp.GetRequiredService<BinanceFuturesClient>(),
    sp.GetService<BinanceSpotClient>(),
    sp.GetRequiredService<CandleCsvWriter>(),
    sp.GetRequiredService<FeedCsvWriter>(),
    sp.GetRequiredService<FeedSchemaManager>(),
    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SymbolCollector>>()));
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
