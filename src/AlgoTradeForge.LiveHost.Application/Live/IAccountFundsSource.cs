using AlgoTradeForge.Domain;

namespace AlgoTradeForge.LiveHost.Application.Live;

public readonly record struct AccountFunds(long FreeScaled, string QuoteAsset);

public interface IAccountFundsSource
{
    // TODO: carry full units (amount + currency + scale) and propagate a Money model into
    // Domain.Portfolio before prod — AccountFunds.QuoteAsset is the first step; the shared
    // Portfolio ledger is still a unit-less long, fenced to one currency by CoTenancyRule.
    // `account` scopes discovery to one sub-account (an IB login spans N accounts on one socket).
    Task<AccountFunds> DiscoverFunds(string account, Asset asset, CancellationToken ct = default);
}
