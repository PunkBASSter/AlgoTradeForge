using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Aggregation.Jobs;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Aggregation;

/// <summary>
/// Hosted worker pool that drains the aggregation job queue (TRD §6.5). Spawns
/// <c>aggregator.maxConcurrentJobs</c> long-lived worker tasks, each running one
/// <see cref="AggregationPipeline"/> per dequeued job to completion. Mirrors the lifecycle
/// pattern of <c>ScheduledCollectorService</c> for cancellation + logging discipline.
/// </summary>
public sealed class AggregationWorkerHost : BackgroundService
{
    private readonly IAggregationJobQueue _queue;
    private readonly IAggregationJobRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<HistoryLoaderOptions> _options;
    private readonly ILogger<AggregationWorkerHost> _logger;

    public AggregationWorkerHost(
        IAggregationJobQueue queue,
        IAggregationJobRegistry registry,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<HistoryLoaderOptions> options,
        ILogger<AggregationWorkerHost> logger)
    {
        _queue = queue;
        _registry = registry;
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var concurrency = _options.CurrentValue.Aggregator.MaxConcurrentJobs;
        if (concurrency < 1)
        {
            _logger.LogWarning("Aggregator MaxConcurrentJobs={Concurrency} — disabling worker pool.", concurrency);
            return;
        }

        _logger.LogInformation("Aggregation worker host starting with {Concurrency} workers.", concurrency);

        var workers = Enumerable.Range(0, concurrency)
            .Select(i => Task.Run(() => RunWorkerAsync(i, stoppingToken), stoppingToken))
            .ToArray();

        await Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(int workerId, CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    _registry.OnStarted(job.JobId, job.Source.FeedId);

                    using var scope = _scopeFactory.CreateScope();
                    var pipeline = scope.ServiceProvider.GetRequiredService<AggregationPipeline>();

                    var result = pipeline.Run(
                        job,
                        onProgress: ev => RouteProgress(job.JobId, ev),
                        ct: stoppingToken);

                    _registry.OnCompleted(job.JobId, result);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation(
                        "Aggregation worker {WorkerId} canceling job {JobId} due to host shutdown.",
                        workerId, job.JobId);
                    _registry.OnErrored(job.JobId, "host_shutdown",
                        "Job interrupted by host shutdown.", retryable: true);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Aggregation job {JobId} (feedId={FeedId}) failed.",
                        job.JobId, job.OutcomeFeedId);
                    // Redacted message: full ex (incl. stack, paths) is logged above; SSE / snapshot
                    // consumers only see the type name + job_id correlation hook. ex.Message can
                    // include absolute paths, environment data, and OS handle detail on IOException.
                    var redacted = $"Aggregation job failed ({ex.GetType().Name}); see server logs (job_id={job.JobId}).";
                    _registry.OnErrored(job.JobId, "internal_error", redacted, retryable: false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Aggregation worker {WorkerId} crashed.", workerId);
        }
    }

    /// <summary>
    /// Routes pipeline-emitted events into the registry. The pipeline emits Started + Complete
    /// alongside Progress, but the worker drives the state machine itself via <c>OnStarted</c> /
    /// <c>OnCompleted</c> — Started/Complete from the pipeline are silently ignored to avoid
    /// duplicate event-log entries.
    /// </summary>
    private void RouteProgress(string jobId, ProgressEvent ev)
    {
        if (ev is ProgressEvent.Progress p)
        {
            _registry.OnProgress(jobId, p.CurrentPartition, p.BarsEmitted, p.ElapsedMs);
        }
    }
}
