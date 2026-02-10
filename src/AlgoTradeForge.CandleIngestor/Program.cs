using AlgoTradeForge.CandleIngestor;
using AlgoTradeForge.CandleIngestor.DataSourceAdapters;
using AlgoTradeForge.CandleIngestor.Storage;
using AlgoTradeForge.Domain.History;
using Microsoft.Extensions.Options;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog(cfg => cfg.ReadFrom.Configuration(builder.Configuration));

builder.Services.Configure<CandleIngestorOptions>(
    builder.Configuration.GetSection("CandleIngestor"));

builder.Services.AddHttpClient<BinanceAdapter>();

var adaptersConfig = builder.Configuration
    .GetSection("CandleIngestor:Adapters")
    .GetChildren();

foreach (var adapterSection in adaptersConfig)
{
    var adapterOptions = new AdapterOptions
    {
        Type = adapterSection["Type"]!,
        BaseUrl = adapterSection["BaseUrl"]!,
        RateLimitPerMinute = int.Parse(adapterSection["RateLimitPerMinute"] ?? "1200"),
        RequestDelayMs = int.Parse(adapterSection["RequestDelayMs"] ?? "100")
    };

    if (adapterOptions.Type == "Binance")
    {
        builder.Services.AddKeyedSingleton<IDataAdapter>(adapterSection.Key, (sp, _) =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient(nameof(BinanceAdapter));
            return new BinanceAdapter(httpClient, adapterOptions);
        });
    }
    else if (adapterOptions.Type == "LocalCsv")
    {
        builder.Services.AddKeyedSingleton<IDataAdapter>(adapterSection.Key, (sp, _) =>
            new LocalCsvAdapter(adapterOptions, sp.GetRequiredService<ILogger<LocalCsvAdapter>>()));
    }
}

builder.Services.AddSingleton(sp =>
    new CsvCandleWriter(sp.GetRequiredService<IOptions<CandleIngestorOptions>>().Value.DataRoot));
builder.Services.AddSingleton<IngestionOrchestrator>();
builder.Services.AddHostedService<IngestionWorker>();

var host = builder.Build();
await host.RunAsync();
