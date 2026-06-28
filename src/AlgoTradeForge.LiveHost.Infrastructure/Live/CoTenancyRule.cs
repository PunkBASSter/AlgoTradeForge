using AlgoTradeForge.Domain;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live;

// One broker account shares one Portfolio — a unit-less long ledger. A co-tenant session may attach
// only if its money semantics match the account's seed: same price SCALE (tick) AND same quote
// CURRENCY. Either mismatch would silently corrupt the shared cash/margin, so attach fails closed.
// Both checks are against the target's immutable seed (set under the router gate at creation), so
// concurrent session starts cannot slip a mismatched co-tenant past the fence.
internal static class CoTenancyRule
{
    // Null = the session may co-tenant the target; non-null = the human-readable rejection reason.
    public static string? Conflict(AccountTarget target, Asset sessionAsset, string sessionQuoteAsset)
    {
        if (target.SeedAsset.TickSize != sessionAsset.TickSize)
            return $"Session asset '{sessionAsset.Name}' (tick {sessionAsset.TickSize}) cannot share account " +
                   $"'{target.AccountName}' seeded by '{target.SeedAsset.Name}' (tick {target.SeedAsset.TickSize}) " +
                   $"— one account shares one money scale.";

        if (!string.Equals(target.SeedQuoteAsset, sessionQuoteAsset, StringComparison.OrdinalIgnoreCase))
            return $"Session asset '{sessionAsset.Name}' quotes in '{sessionQuoteAsset}', but account " +
                   $"'{target.AccountName}' is seeded in '{target.SeedQuoteAsset}' " +
                   $"— one account shares one quote currency.";

        return null;
    }
}
