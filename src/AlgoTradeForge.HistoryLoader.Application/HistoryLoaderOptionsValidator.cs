using Cronos;
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

        if (options.Binance.MaxWeightPerMinute <= 0)
            failures.Add("Binance.MaxWeightPerMinute must be greater than 0.");

        if (options.CircuitBreakerCooldownMinutes <= 0)
            failures.Add("CircuitBreakerCooldownMinutes must be greater than 0.");

        if (options.Aggregator.MaxPartitionSizeMB <= 0)
            failures.Add("Aggregator.MaxPartitionSizeMB must be greater than 0.");
        if (options.Aggregator.MaxConcurrentJobs <= 0)
            failures.Add("Aggregator.MaxConcurrentJobs must be greater than 0.");
        if (options.Aggregator.MaxConcurrentTickJobs <= 0)
            failures.Add("Aggregator.MaxConcurrentTickJobs must be greater than 0.");
        if (options.Aggregator.MaxQueueDepth <= 0)
            failures.Add("Aggregator.MaxQueueDepth must be greater than 0.");
        if (options.Aggregator.JobRetentionMinutes <= 0)
            failures.Add("Aggregator.JobRetentionMinutes must be greater than 0.");

        if (options.Load.MaxTickMonthsPerRequest <= 0)
            failures.Add("Load.MaxTickMonthsPerRequest must be greater than 0.");

        foreach (var (key, schedule) in options.Schedules)
        {
            try { CronExpression.Parse(schedule.Cron); }
            catch (CronFormatException)
            {
                failures.Add($"Schedule '{key}': '{schedule.Cron}' is not a valid cron expression.");
            }

            try { TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZone); }
            catch (TimeZoneNotFoundException)
            {
                failures.Add($"Schedule '{key}': TimeZone '{schedule.TimeZone}' is not valid.");
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
