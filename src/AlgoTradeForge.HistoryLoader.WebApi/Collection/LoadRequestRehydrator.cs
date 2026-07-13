using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Application.Jobs;

namespace AlgoTradeForge.HistoryLoader.WebApi.Collection;

// The row's feed_key alone cannot rebuild the CollectionAsset or the [from,to] window — that
// is why request_json is persisted at create time. Rehydrate resolves the live asset from the
// current plan by (exchange, apiSymbol, assetType) — the SAME lookup PostLoad does on create.
internal sealed class LoadRequestRehydrator(ICollectionPlanSource planSource)
{
    private sealed record Payload(
        string Exchange, string Symbol, string AssetType, string Feed, string Interval, DateOnly From, DateOnly To);

    public static string Serialize(
        string exchange, string symbol, string assetType, string feed, string interval, DateOnly from, DateOnly to) =>
        JsonSerializer.Serialize(new Payload(exchange, symbol, assetType, feed, interval, from, to));

    public ArchiveLoadRequest Rehydrate(IndexJobRow row)
    {
        if (string.IsNullOrEmpty(row.RequestJson))
            throw new InvalidOperationException($"Load job '{row.Id}' has no request_json to rehydrate.");

        var p = JsonSerializer.Deserialize<Payload>(row.RequestJson)
            ?? throw new InvalidOperationException($"Load job '{row.Id}' request_json deserialized to null.");

        var asset = planSource.Current.Assets.FirstOrDefault(a =>
            string.Equals(a.Exchange, p.Exchange, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Venue.ApiSymbol, p.Symbol, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Venue.AssetType, p.AssetType, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Load job '{row.Id}' references symbol '{p.Symbol}' ({p.AssetType}) no longer declared in any enabled group.");

        return new ArchiveLoadRequest(asset, p.Feed, p.Interval, p.From, p.To);
    }
}
