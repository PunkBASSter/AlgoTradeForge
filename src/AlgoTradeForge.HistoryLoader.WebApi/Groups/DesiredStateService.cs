using AlgoTradeForge.HistoryLoader.Application.Groups;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.WebApi.Groups;

internal sealed class DesiredStateService(
    IGroupStore store,
    ConvergenceEvaluator evaluator,
    ILogger<DesiredStateService> logger) : BackgroundService
{
    private volatile ConvergenceReport? _report;
    private CancellationTokenSource? _debounceCts;
    private readonly object _lock = new();
    private CancellationToken _stopping;

    public ConvergenceReport? LatestReport => _report;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;

        // First compute runs unconditionally — LegacyImportService has already written any
        // groups to disk at this point, so the expansion sees them without a startup race.
        await ComputeReport(stoppingToken);

        // Subscribe AFTER the first compute so GroupsChanged only triggers recomputes.
        store.GroupsChanged += OnGroupsChanged;
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
        finally
        {
            store.GroupsChanged -= OnGroupsChanged;
            lock (_lock)
            {
                _debounceCts?.Cancel();
                _debounceCts?.Dispose();
                _debounceCts = null;
            }
        }
    }

    private void OnGroupsChanged()
    {
        CancellationToken debounceCt;
        lock (_lock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
            debounceCt = _debounceCts.Token;
        }
        _ = RunDebounced(debounceCt);
    }

    private async Task RunDebounced(CancellationToken ct)
    {
        try
        {
            await Task.Delay(500, ct);
            await ComputeReport(ct);
        }
        catch (OperationCanceledException)
        {
            // debounced away or shutting down
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "desired-state: recompute failed");
        }
    }

    private async Task ComputeReport(CancellationToken ct)
    {
        try
        {
            var docs = await store.List(ct);
            var groups = docs.Select(d => d.Group).ToList();
            _report = await evaluator.Evaluate(groups, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "desired-state: compute failed");
        }
    }
}
