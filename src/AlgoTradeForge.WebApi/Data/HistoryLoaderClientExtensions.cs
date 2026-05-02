using Microsoft.Extensions.Options;

namespace AlgoTradeForge.WebApi.Data;

/// <summary>
/// DI registration helpers for the typed <see cref="HistoryLoaderClient"/>. Keeps the
/// <c>Program.cs</c> wire-up small and centralizes the options-binding + base-URL/timeout
/// translation in one place (so test harnesses can swap the registration without touching
/// production paths).
/// </summary>
public static class HistoryLoaderClientExtensions
{
    /// <summary>
    /// Binds <see cref="HistoryLoaderOptions"/> from the <c>"HistoryLoader"</c> config
    /// section, registers the typed <see cref="HistoryLoaderClient"/>, and configures its
    /// underlying <c>HttpClient</c> with <c>BaseAddress</c> + non-SSE
    /// <see cref="HttpClient.Timeout"/>. Trailing slashes on <c>BaseUrl</c> are stripped at
    /// bind time so call sites pass relative paths starting with <c>"/"</c>.
    /// </summary>
    public static IServiceCollection AddHistoryLoaderClient(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<HistoryLoaderOptions>(configuration.GetSection("HistoryLoader"));

        services.AddHttpClient<HistoryLoaderClient>((sp, http) =>
        {
            var opts = sp.GetRequiredService<IOptions<HistoryLoaderOptions>>().Value;
            // HttpClient.BaseAddress combine rules require a trailing slash to preserve any
            // path component in BaseUrl. We always send absolute-path relatives ("/api/v1/...")
            // so it doesn't strictly matter, but we normalize defensively: trim any trailing
            // slash the user pasted, then re-append exactly one.
            var normalized = opts.BaseUrl.TrimEnd('/') + "/";
            http.BaseAddress = new Uri(normalized);
            http.Timeout = opts.RequestTimeout;
        });

        return services;
    }
}
