using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Application;

public sealed class HistoryLoaderOptionsValidator : IValidateOptions<HistoryLoaderOptions>
{
    public ValidateOptionsResult Validate(string? name, HistoryLoaderOptions options)
    {
        var failures = new List<string>();

        if (options.MaxBackfillConcurrency <= 0)
            failures.Add("MaxBackfillConcurrency must be greater than 0.");

        if (options.Binance.WeightBudgetPercent is < 1 or > 100)
            failures.Add("Binance.WeightBudgetPercent must be between 1 and 100.");

        if (options.CircuitBreakerCooldownMinutes <= 0)
            failures.Add("CircuitBreakerCooldownMinutes must be greater than 0.");

        foreach (var asset in options.Assets)
        {
            if (asset.DecimalDigits is < 0 or > 18)
                failures.Add($"Asset {asset.Symbol}: DecimalDigits must be between 0 and 18.");

            foreach (var feed in asset.Feeds)
            {
                if (feed.GapThresholdMultiplier <= 1.0)
                    failures.Add($"Asset {asset.Symbol}, feed {feed.Name}: GapThresholdMultiplier must be greater than 1.0.");
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
