using System.Globalization;
using AlgoTradeForge.Domain;
using AlgoTradeForge.LiveHost.Application.Live;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;

public sealed class BinanceAccountFundsSource(BinanceApiClient apiClient) : IAccountFundsSource
{
    public async Task<AccountFunds> DiscoverFunds(Asset asset, CancellationToken ct = default)
    {
        var symbolInfo = await apiClient.GetExchangeInfoAsync(asset.Name, ct);
        var accountInfo = await apiClient.GetAccountInfoAsync(ct);

        var quoteBalance = accountInfo.Balances
            .FirstOrDefault(b => b.Asset.Equals(symbolInfo.QuoteAsset, StringComparison.OrdinalIgnoreCase));
        var freeBalance = quoteBalance is not null
            ? decimal.Parse(quoteBalance.Free, CultureInfo.InvariantCulture)
            : 0m;

        var scaled = new ScaleContext(asset).FromMarketPrice(freeBalance);
        return new AccountFunds(scaled, symbolInfo.QuoteAsset);
    }
}
