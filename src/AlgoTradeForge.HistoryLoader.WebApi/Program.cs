using AlgoTradeForge.Storage;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Aggregation.Jobs;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Archive.Jobs;
using AlgoTradeForge.HistoryLoader.Application.Canonicalization;
using AlgoTradeForge.HistoryLoader.Application.Catalog;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;
using AlgoTradeForge.HistoryLoader.WebApi;
using AlgoTradeForge.HistoryLoader.WebApi.Aggregation;
using AlgoTradeForge.HistoryLoader.WebApi.Collection;
using AlgoTradeForge.HistoryLoader.WebApi.Endpoints;
using AlgoTradeForge.HistoryLoader.Infrastructure;
using AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();
builder.Host.UseSystemd();

var logDir = Path.Combine(builder.Environment.ContentRootPath, "logs");
builder.Services.AddSerilog(cfg => cfg
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.File(Path.Combine(logDir, "history-loader-.log"),
        rollingInterval: Serilog.RollingInterval.Day, shared: true));

builder.Services.Configure<HistoryLoaderOptions>(
    builder.Configuration.GetSection("HistoryLoader"));
builder.Services.AddSingleton<IValidateOptions<HistoryLoaderOptions>, HistoryLoaderOptionsValidator>();

builder.Services.Configure<HistoryLoaderStorageOptions>(
    builder.Configuration.GetSection("HistoryLoader:Storage"));

// PR3: Storage:Local:DataRoot drives LocalFileStorage's relative-key resolution. Absolute keys
// still pass through (the writers haven't moved to relative keys yet — that's PR4). PR5: the
// same Storage section now also picks between local FS and S3 via Backend.
builder.Services.Configure<StorageOptions>(
    builder.Configuration.GetSection("Storage"));
foreach (var warning in StorageConfigMigration.ApplyLegacyAliases(builder.Configuration, builder.Services))
    Console.Error.WriteLine($"[Storage] {warning}");

// API JSON: snake_case wire schema. Distinct from the camelCase FeedSchemaManager uses for
// on-disk feeds.json (its own JsonOptions).
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.PropertyNameCaseInsensitive = false;
    o.SerializerOptions.DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
});

builder.Services.AddHealthChecks();

builder.Services.AddHistoryLoaderInfrastructure();

builder.Services.Configure<CanonicalizerOptions>(
    builder.Configuration.GetSection(CanonicalizerOptions.SectionName));
builder.Services.PostConfigure<CanonicalizerOptions>(opt =>
{
    if (string.IsNullOrEmpty(opt.AssetDirBase))
        opt.AssetDirBase = builder.Configuration.GetSection("Storage:Local:DataRoot").Value ?? "";

    // Per-instrument tick scale so TradeProjection stores price/qty as scaled long (matches the
    // HistoryLoader tick writers); absent instruments fall back to the canonical exponent.
    var assets = builder.Configuration.GetSection("HistoryLoader").Get<HistoryLoaderOptions>()?.Assets ?? [];
    foreach (var asset in assets)
        opt.InstrumentDecimalDigits.TryAdd(asset.Symbol, asset.DecimalDigits);
});
builder.Services.AddTickCanonicalizer();
builder.Services.AddHostedService<TickCanonicalizerService>();

builder.Services.AddSingleton<IFeedCollector, CandleFeedCollector>();
builder.Services.AddSingleton<IFeedCollector, FundingRateFeedCollector>();
builder.Services.AddSingleton<IFeedCollector, MarkPriceFeedCollector>();
builder.Services.AddSingleton<IFeedCollector, PremiumIndexFeedCollector>();
builder.Services.AddSingleton<IFeedCollector, IndexPriceFeedCollector>();
builder.Services.AddSingleton<IFeedCollector, OpenInterestFeedCollector>();
builder.Services.AddSingleton<IFeedCollector, LsRatioGlobalFeedCollector>();
builder.Services.AddSingleton<IFeedCollector, LsRatioTopAccountsFeedCollector>();
builder.Services.AddSingleton<IFeedCollector, TakerVolumeFeedCollector>();
builder.Services.AddSingleton<IFeedCollector, LsRatioTopPositionsFeedCollector>();
builder.Services.AddSingleton<IFeedCollector, LiquidationFeedCollector>();
builder.Services.AddSingleton<IFeedCollector, AggTradeFeedCollector>();

// Persists discovered feed dates back to appsettings.json. Binds LocalFileStorage directly
// (not IFileStorage) because the binary's content-root appsettings.json is host config and
// must never be routed to S3.
var appSettingsPath = Path.Combine(builder.Environment.ContentRootPath, "appsettings.json");
builder.Services.AddSingleton<ISettingsWriter>(sp =>
    new AppSettingsWriter(
        appSettingsPath,
        new AlgoTradeForge.Storage.LocalFileStorage(),
        sp.GetRequiredService<ILogger<AppSettingsWriter>>()));

builder.Services.AddSingleton<ICollectionCircuitBreaker, CollectionCircuitBreaker>();
builder.Services.AddSingleton<SymbolCollector>();
builder.Services.AddSingleton<CollectionPolicy>();
builder.Services.AddSingleton<BackfillOrchestrator>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IFeedCatalog, FeedCatalog>();
builder.Services.AddSingleton<IAggregationJobRegistry, AggregationJobRegistry>();
builder.Services.AddHostedService<LoadJobWorker>();
builder.Services.AddSingleton<IAggregationJobQueue, AggregationJobQueue>();
builder.Services.AddSingleton<IAggregationTickJobQueue, AggregationTickJobQueue>();
builder.Services.AddScoped<PartitionedSourceReader>();
builder.Services.AddScoped<OverwritePathWriter>();
builder.Services.AddScoped<AggregationPipeline>();
builder.Services.AddHostedService<AggregationWorkerHost>();

// Sweep MUST run before any collector hosted service so orphan staging/tmp left by a prior
// crash is gone before workers start.
builder.Services.AddHostedService<StartupSweepService>();

builder.Services.AddHostedService<KlineCollectorService>();
builder.Services.AddHostedService<FundingRateCollectorService>();
builder.Services.AddHostedService<OiCollectorService>();
builder.Services.AddHostedService<RatioCollectorService>();
builder.Services.AddHostedService<HourlyCollectorService>();
builder.Services.AddHostedService<LiquidationStreamService>();
builder.Services.AddHostedService<TicksCollectorService>();
builder.Services.AddHostedService<FundingInfoRefreshService>();
builder.Services.AddHostedService<SpotAggTradeStreamService>();
builder.Services.AddHostedService<BookTickerStreamService>();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapStatusEndpoints();
app.MapBackfillEndpoints();
app.MapCatalogEndpoints();
app.MapAggregationEndpoints();
app.MapLoadEndpoints();
app.MapCoverageEndpoints();

app.Run();
