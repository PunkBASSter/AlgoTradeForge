using System.Threading.Channels;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Aggregation.Jobs;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Aggregation;

/// <summary>
/// Hosted worker pools that drain the aggregation job queues. Two separate pools (time-bar and
/// tick) prevent I/O-heavy tick jobs from blocking CPU-heavy time-bar jobs at the queue head.
/// </summary>
public sealed class AggregationWorkerHost : BackgroundService
{
    private readonly IAggregationJobQueue _timeBarQueue;
    private readonly IAggregationTickJobQueue _tickQueue;
    private readonly IAggregationJobRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<HistoryLoaderOptions> _options;
    private readonly ILogger<AggregationWorkerHost> _logger;

    public AggregationWorkerHost(
        IAggregationJobQueue timeBarQueue,
        IAggregationTickJobQueue tickQueue,
        IAggregationJobRegistry registry,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<HistoryLoaderOptions> options,
        ILogger<AggregationWorkerHost> logger)
    {
        _timeBarQueue = timeBarQueue;
        _tickQueue = tickQueue;
        _registry = registry;
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var aggregator = _options.CurrentValue.Aggregator;
        var timeBarConcurrency = aggregator.MaxConcurrentJobs;
        var tickConcurrency = aggregator.MaxConcurrentTickJobs;

        if (timeBarConcurrency < 1 && tickConcurrency < 1)
        {
            _logger.LogWarning(
                "Aggregator MaxConcurrentJobs={TimeBar} and MaxConcurrentTickJobs={Tick} both < 1 — disabling worker host.",
                timeBarConcurrency, tickConcurrency);
            return;
        }

        _logger.LogInformation(
            "Aggregation worker host starting with {TimeBar} time-bar workers + {Tick} tick workers.",
            timeBarConcurrency, tickConcurrency);

        var workers = new List<Task>(timeBarConcurrency + tickConcurrency);
        for (int i = 0; i < timeBarConcurrency; i++)
        {
            int workerId = i;
            workers.Add(Task.Run(() => RunWorkerAsync("timebar", workerId, _timeBarQueue.Reader, stoppingToken), stoppingToken));
        }
        for (int i = 0; i < tickConcurrency; i++)
        {
            int workerId = i;
            workers.Add(Task.Run(() => RunWorkerAsync("tick", workerId, _tickQueue.Reader, stoppingToken), stoppingToken));
        }

        await Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(
        string poolName,
        int workerId,
        ChannelReader<AggregationJob> reader,
        CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in reader.ReadAllAsync(stoppingToken))
            {
                // Link the host stopping token with the per-job CTS for user cancels. The
                // pipeline observes either source via ct.ThrowIfCancellationRequested(); the
                // catch below distinguishes which source fired to route OnCancelled vs
                // OnErrored("host_shutdown").
                var perJobToken = _registry.GetCancellationToken(job.JobId);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, perJobToken);

                try
                {
                    _registry.OnStarted(job.JobId, job.Source.FeedId);

                    using var scope = _scopeFactory.CreateScope();
                    var pipeline = scope.ServiceProvider.GetRequiredService<AggregationPipeline>();

                    var result = await pipeline.Run(
                        job,
                        onProgress: ev => RouteProgress(job.JobId, ev),
                        ct: linkedCts.Token);

                    _registry.OnCompleted(job.JobId, result);
                }
                catch (OperationCanceledException) when (perJobToken.IsCancellationRequested)
                {
                    // User cancel — distinct terminal state from host_shutdown. Don't rethrow:
                    // the worker continues draining the queue.
                    _logger.LogInformation(
                        "Aggregation worker {Pool}#{WorkerId} canceling job {JobId} on user request.",
                        poolName, workerId, job.JobId);
                    _registry.OnCancelled(job.JobId, "user_cancelled");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation(
                        "Aggregation worker {Pool}#{WorkerId} canceling job {JobId} due to host shutdown.",
                        poolName, workerId, job.JobId);
                    _registry.OnErrored(job.JobId, "host_shutdown",
                        "Job interrupted by host shutdown.", retryable: true);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Aggregation job {JobId} (feedId={FeedId}, pool={Pool}) failed.",
                        job.JobId, job.OutcomeFeedId, poolName);
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
            _logger.LogCritical(ex, "Aggregation worker {Pool}#{WorkerId} crashed.", poolName, workerId);
        }
    }

    private void RouteProgress(string jobId, ProgressEvent ev)
    {
        if (ev is ProgressEvent.Progress p)
        {
            _registry.OnProgress(jobId, p.CurrentPartition, p.BarsEmitted, p.ElapsedMs);
        }
    }
}
