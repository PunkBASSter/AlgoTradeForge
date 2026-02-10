using AlgoTradeForge.Application;
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Infrastructure.CandleIngestion;
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
builder.Services.AddSingleton<IMetricsCalculator, MetricsCalculator>();
builder.Services.AddSingleton<BacktestEngine>();

// Register Application services
builder.Services.AddApplication();

// Register Infrastructure services
builder.Services.Configure<CandleStorageOptions>(
    builder.Configuration.GetSection("CandleStorage"));
builder.Services.AddSingleton<IInt64BarLoader, CsvInt64BarLoader>();

// NOTE: IAssetRepository, IBarSourceRepository, and IStrategyFactory
// must be registered by an Infrastructure layer or test configuration

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

app.UseHttpsRedirection();

// Map endpoints
app.MapBacktestEndpoints();

app.Run();
