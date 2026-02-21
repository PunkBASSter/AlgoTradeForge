using AlgoTradeForge.Application;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Infrastructure;
using AlgoTradeForge.Infrastructure.CandleIngestion;
using AlgoTradeForge.Infrastructure.History;
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
builder.Services.AddSingleton<IRiskEvaluator, BasicRiskEvaluator>();
builder.Services.AddSingleton<IMetricsCalculator, MetricsCalculator>();
builder.Services.AddSingleton<BacktestEngine>();

// Register Application services
builder.Services.AddApplication();

// Register Infrastructure services
builder.Services.Configure<CandleStorageOptions>(
    builder.Configuration.GetSection("CandleStorage"));
builder.Services.AddSingleton<IInt64BarLoader, CsvInt64BarLoader>();
builder.Services.AddSingleton<IDataSource, CsvDataSource>();
builder.Services.AddSingleton<IHistoryRepository, HistoryRepository>();

// Register optimization infrastructure
builder.Services.AddInfrastructure(typeof(AlgoTradeForge.Domain.Strategy.StrategyBase<>).Assembly);

builder.Services.AddSingleton<IAssetRepository, InMemoryAssetRepository>();

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
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

// Map endpoints
app.MapBacktestEndpoints();
app.MapOptimizationEndpoints();
app.MapDebugEndpoints();
app.MapDebugWebSocket();

app.Run();
