using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Storage;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Canonicalization;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

public static class CanonicalizerServiceCollectionExtensions
{
    public static IServiceCollection AddTickCanonicalizer(this IServiceCollection services)
    {
        services.AddSingleton<ISessionFeedWriter, DailySessionCsvWriter>();

        services.AddSingleton(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<CanonicalizerOptions>>().Value;
            var planSource = sp.GetRequiredService<ICollectionPlanSource>();
            return new InstrumentAssetDirMap(opt.AssetDirBase, planSource);
        });

        services.AddSingleton<IStreamCursorStore, FileStreamCursorStore>();

        services.AddSingleton<IStreamProjection<TradeTick>, TradeProjection>();
        services.AddSingleton<IStreamProjection<QuoteTick>, QuoteProjection>();
        services.AddSingleton<IStreamProjection<SessionEvent>, SessionProjection>();

        services.AddSingleton<IStreamCanonicalizer>(sp => Build<TradeTick>(sp));
        services.AddSingleton<IStreamCanonicalizer>(sp => Build<QuoteTick>(sp));
        services.AddSingleton<IStreamCanonicalizer>(sp => Build<SessionEvent>(sp));

        return services;
    }

    private static StreamCanonicalizer<T> Build<T>(IServiceProvider sp) where T : IFramePayload<T>
    {
        var opt = sp.GetRequiredService<IOptions<CanonicalizerOptions>>().Value;
        return new StreamCanonicalizer<T>(
            sp.GetRequiredService<IFileStorage>(),
            sp.GetRequiredService<IStreamProjection<T>>(),
            sp.GetRequiredService<IStreamCursorStore>(),
            opt.LiveMdPrefix,
            opt.CursorPrefix);
    }
}
