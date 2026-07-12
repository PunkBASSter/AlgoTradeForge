using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Domain.Symbology;

namespace AlgoTradeForge.HistoryLoader.Application.Groups;

/// <summary>Semantic (registry-aware) validation, complementing the structural
/// <see cref="GroupValidator"/>. A feed declared <c>on-demand</c> that no archive materializer can
/// replenish — for ANY of the group's (exchange × asset-type) combinations — would never be
/// collected: streams only subscribe eager feeds, and the on-demand load path rejects
/// non-replenishable feeds. Rejecting it makes an unreachable config fail loudly instead of
/// silently dropping data (the safety net the retired CollectionPolicy provided).</summary>
public static class GroupCollectabilityValidator
{
    public static IReadOnlyList<string> Validate(
        CollectionGroup group, ArchiveMaterializerRegistry registry)
    {
        var errors = new List<string>();

        if (group.Exchanges is null or { Count: 0 })
            return errors;   // structural validator flags empty exchanges

        var assetTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in group.Assets.Symbols)
            if (CanonicalSymbolParser.TryParse(symbol, out var parsed, out _))
                assetTypes.Add(AssetTypeOf(parsed!.Kind));

        if (assetTypes.Count == 0)
            return errors;   // no parseable symbols — structural validator flags them

        foreach (var (feedName, feedDef) in group.Feeds)
        {
            if (feedDef.Collect != "on-demand")
                continue;

            var replenishableSomewhere = group.Exchanges.Any(ex =>
                assetTypes.Any(at => registry.IsReplenishable(ex, feedName, at)));

            if (!replenishableSomewhere)
                errors.Add(
                    $"feed '{feedName}' is declared on-demand but no archive materializer can " +
                    "replenish it for this group's assets — it would never be collected; " +
                    "declare it eager or remove it");
        }

        return errors;
    }

    private static string AssetTypeOf(InstrumentKind kind) => kind switch
    {
        InstrumentKind.Perpetual   => AssetTypes.Perpetual,
        InstrumentKind.DatedFuture => AssetTypes.Future,
        _                          => AssetTypes.Spot,
    };
}
