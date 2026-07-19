using AlgoTradeForge.Storage;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Groups;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Aggregation.Jobs;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Canonicalization;
using AlgoTradeForge.HistoryLoader.Application.Catalog;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;
using AlgoTradeForge.HistoryLoader.Application.Jobs;
using AlgoTradeForge.HistoryLoader.Domain.Symbology;
using AlgoTradeForge.HistoryLoader.WebApi;
using AlgoTradeForge.HistoryLoader.WebApi.Aggregation;
using AlgoTradeForge.HistoryLoader.WebApi.Collection;
using AlgoTradeForge.HistoryLoader.WebApi.Endpoints;
using AlgoTradeForge.HistoryLoader.WebApi.Groups;
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

// Dev isolation by construction: under Development, the data/config/index roots default to a
// dedicated HistoryDev tree so a dev run can NEVER read or write the production History/index.
// Only fills roots left unset — an explicit config value or HistoryLoader__* env var still wins
// (set them to point a dev run at production data on purpose). Production is untouched.
if (builder.Environment.IsDevelopment())
{
    var devRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AlgoTradeForge", "HistoryDev");
    var devDefaults = new Dictionary<string, string?>();
    if (string.IsNullOrEmpty(builder.Configuration["HistoryLoader:DataRoot"]))
        devDefaults["HistoryLoader:DataRoot"] = devRoot;
    if (string.IsNullOrEmpty(builder.Configuration["HistoryLoader:ConfigRoot"]))
        devDefaults["HistoryLoader:ConfigRoot"] = Path.Combine(devRoot, "config");
    if (string.IsNullOrEmpty(builder.Configuration["HistoryLoader:Index:Path"]))
        devDefaults["HistoryLoader:Index:Path"] = Path.Combine(devRoot, "history-index-dev.sqlite");
    if (devDefaults.Count > 0)
        builder.Configuration.AddInMemoryCollection(devDefaults);
}

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
builder.Services.AddSingleton<IFeedCollector, LsRatioTopPositionsFeedCollector>();
builder.Services.AddSingleton<IFeedCollector, LiquidationFeedCollector>();
builder.Services.AddSingleton<IFeedCollector, AggTradeFeedCollector>();

builder.Services.AddSingleton<CollectionChangeNotifier>();

builder.Services.AddSingleton<ICollectionCircuitBreaker, CollectionCircuitBreaker>();
builder.Services.AddSingleton<SymbolCollector>();
builder.Services.AddSingleton<CollectionPlanHolder>();
builder.Services.AddSingleton<ICollectionPlanSource>(sp => sp.GetRequiredService<CollectionPlanHolder>());
builder.Services.AddSingleton<BackfillOrchestrator>();
builder.Services.AddSingleton<IEagerBackfillRunner, EagerBackfillRunner>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IJobEventSignal, JobEventSignal>();
builder.Services.AddSingleton<IJobProgressSinkFactory, JobProgressSinkFactory>();
builder.Services.AddSingleton<IJobCancellationMap, JobCancellationMap>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IFeedCatalog, FeedCatalog>();
builder.Services.AddSingleton<IBackfillOrchestrator>(sp => sp.GetRequiredService<BackfillOrchestrator>());
builder.Services.AddSingleton<IArchiveLoadService, ArchiveLoadService>();
builder.Services.AddSingleton<LoadRequestRehydrator>();
builder.Services.AddHostedService<LoadJobWorker>();
builder.Services.AddHostedService<AlgoTradeForge.HistoryLoader.WebApi.Jobs.JobRetentionSweeper>();
builder.Services.AddSingleton<IAggregationJobQueue, AggregationJobQueue>();
builder.Services.AddSingleton<IAggregationTickJobQueue, AggregationTickJobQueue>();
builder.Services.AddScoped<PartitionedSourceReader>();
builder.Services.AddScoped<OverwritePathWriter>();
// D1: AggregationService (M3.2) resolves IAggregationPipeline per-scope; register the seam.
builder.Services.AddScoped<IAggregationPipeline, AggregationPipeline>();
builder.Services.AddSingleton<IAggregationService, AggregationService>();
builder.Services.AddSingleton<AggregationRequestRehydrator>();
builder.Services.AddHostedService<AggregationWorkerHost>();
builder.Services.AddSingleton<AlgoTradeForge.HistoryLoader.WebApi.Jobs.IMaterializeStageRequestFactory,
    AlgoTradeForge.HistoryLoader.WebApi.Jobs.MaterializeStageRequestFactory>();
builder.Services.AddHostedService<AlgoTradeForge.HistoryLoader.WebApi.Jobs.MaterializeWorkerHost>();

// Sweep MUST run before any collector hosted service so orphan staging/tmp left by a prior
// crash is gone before workers start.
builder.Services.AddHostedService<StartupSweepService>();
// §S8: InterruptedJobSweeper is an IHostedService whose StartAsync is AWAITED by the host in
// registration order — it completes BEFORE DesiredStateService's first convergence (registered
// below), so a mid-flight month left by a crash is reconciled out of the index before the
// reconciler can read it as complete and suppress re-collection.
builder.Services.AddHostedService<AlgoTradeForge.HistoryLoader.WebApi.Jobs.InterruptedJobSweeper>();
builder.Services.AddHostedService<AlgoTradeForge.HistoryLoader.WebApi.Index.IndexMaintenanceService>();
builder.Services.AddHostedService<AlgoTradeForge.HistoryLoader.WebApi.Index.DriftSweepService>();

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

builder.Services.AddSingleton<IExchangeSymbology, BinanceSymbology>();
builder.Services.AddSingleton<SymbologyRegistry>();
builder.Services.AddSingleton<IGroupStore, GroupStore>();
// Ordering: LegacyImportService BEFORE DesiredStateService — writes groups once at startup so
// the reconciler's first compute sees them without a race.
builder.Services.AddHostedService<LegacyImportService>();
builder.Services.AddSingleton<ConvergenceEvaluator>();
builder.Services.AddSingleton<DesiredStateService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DesiredStateService>());

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapStatusEndpoints();
app.MapBackfillEndpoints();
app.MapCatalogEndpoints();
app.MapAggregationEndpoints();
app.MapLoadEndpoints();
app.MapMaterializeEndpoints();
app.MapCoverageEndpoints();
app.MapGroupEndpoints();
app.MapDesiredStateEndpoints();
app.MapJobEndpoints();
app.MapMaintenanceEndpoints();

app.Run();
