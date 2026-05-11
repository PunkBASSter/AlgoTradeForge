using Microsoft.Extensions.Options;

namespace AlgoTradeForge.WebApi.Data;

/// <summary>DI registration for the typed <see cref="HistoryLoaderClient"/>.</summary>
public static class HistoryLoaderClientExtensions
{
    public static IServiceCollection AddHistoryLoaderClient(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<HistoryLoaderOptions>(configuration.GetSection("HistoryLoader"));

        services.AddHttpClient<HistoryLoaderClient>((sp, http) =>
        {
            var opts = sp.GetRequiredService<IOptions<HistoryLoaderOptions>>().Value;
            // BaseAddress combine rules require a trailing slash; normalize defensively.
            var normalized = opts.BaseUrl.TrimEnd('/') + "/";
            http.BaseAddress = new Uri(normalized);
            http.Timeout = opts.RequestTimeout;
        });

        return services;
    }
}
