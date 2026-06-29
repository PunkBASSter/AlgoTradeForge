using System.Globalization;
using AlgoTradeForge.Domain;
using AlgoTradeForge.LiveHost.Application.Live;
using IBApi;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Discovers free funds via reqAccountSummary ("AvailableFunds" tag) and scales to the Portfolio's
// long-denominated cash ledger using the same ScaleContext.FromMarketPrice path as BinanceAccountFundsSource.
internal sealed class IbAccountFundsSource(IIbAccountSummaryClient client) : IAccountFundsSource
{
    public async Task<AccountFunds> DiscoverFunds(string account, Asset asset, CancellationToken ct = default)
    {
        var reqId = client.NextReqId();
        var pending = client.RegisterAccountSummary(reqId);
        // IB's group param accepts only "All" or an Advisor-Group name — never a specific account id —
        // so request all accounts and filter the rows by account below (a login spans N sub-accounts).
        client.RequestAccountSummary(reqId, "All", AccountSummaryTags.AvailableFunds);

        var rows = await pending.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);

        var row = rows.FirstOrDefault(r =>
            string.Equals(r.Account, account, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Tag, AccountSummaryTags.AvailableFunds, StringComparison.Ordinal));

        if (string.IsNullOrEmpty(row.Value))
            return new AccountFunds(0L, "");

        var amount = decimal.Parse(row.Value, CultureInfo.InvariantCulture);
        var scaled = new ScaleContext(asset).FromMarketPrice(amount);
        return new AccountFunds(scaled, row.Currency);
    }
}
