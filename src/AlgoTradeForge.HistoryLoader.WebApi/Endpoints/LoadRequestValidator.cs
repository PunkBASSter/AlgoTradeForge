using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.WebApi.Endpoints;

internal sealed record LoadRequest(
    string Exchange, string Symbol, string AssetType,
    string FeedName, string Interval,
    DateOnly From, DateOnly To);

internal sealed record LoadValidationError(string Code, string Message);

internal static class LoadRequestValidator
{
    public static bool IsKnownAssetType(string assetType) =>
        Array.Exists(AssetTypes.All, t => string.Equals(t, assetType, StringComparison.OrdinalIgnoreCase));

    public static LoadValidationError? Validate(
        LoadRequest request,
        ArchiveMaterializerRegistry registry,
        LoadOptions options)
    {
        if (!IsKnownAssetType(request.AssetType))
            return new LoadValidationError("unknown_asset_type",
                $"Unknown asset type '{request.AssetType}'. Valid types: {string.Join(", ", AssetTypes.All)}.");

        if (request.From > request.To)
            return new LoadValidationError("invalid_range",
                $"'from' ({request.From:yyyy-MM-dd}) must not be after 'to' ({request.To:yyyy-MM-dd}).");

        var months = (request.To.Year - request.From.Year) * 12 + (request.To.Month - request.From.Month) + 1;
        if (months > options.MaxMonthsPerRequest)
            return new LoadValidationError("too_many_months",
                $"Request spans {months} months; limit is {options.MaxMonthsPerRequest}.");

        if (!registry.IsReplenishable(request.Exchange, request.FeedName, request.AssetType))
            return new LoadValidationError("not_replenishable",
                $"Feed '{request.FeedName}' is not replenishable for exchange '{request.Exchange}' and asset type '{request.AssetType}'.");

        return null;
    }
}
