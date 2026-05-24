using AlgoTradeForge.HistoryLoader.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Storage.Buffered;

/// <summary>
/// Drives both periodic flush (every <see cref="HistoryLoaderStorageOptions.FlushIntervalSeconds"/>)
/// and graceful-shutdown flush across every <see cref="IBufferedPartitionWriter"/>. A single
/// service for all four writers — no per-writer timer plumbing.
/// </summary>
/// <remarks>
/// Shutdown contract: on <see cref="IHostApplicationLifetime.ApplicationStopping"/>, fire one
/// final <c>FlushAllAsync</c> with a bounded timeout so the host doesn't hang on a wedged S3
/// endpoint. Unflushed rows after the timeout are lost — the buffer-then-PUT model's
/// documented crash window.
/// </remarks>
internal sealed class BufferedWriterFlushService : BackgroundService
{
    private readonly IEnumerable<IBufferedPartitionWriter> _writers;
    private readonly HistoryLoaderStorageOptions _options;
    private readonly ILogger<BufferedWriterFlushService> _logger;

    public BufferedWriterFlushService(
        IEnumerable<IBufferedPartitionWriter> writers,
        IOptions<HistoryLoaderStorageOptions> options,
        ILogger<BufferedWriterFlushService> logger)
    {
        _writers = writers;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.FlushIntervalSeconds));
        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
                await FlushAllOnce(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Periodic flush iteration failed; will retry on next tick");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.ShutdownFlushTimeoutSeconds));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            await FlushAllOnce(cts.Token);
            _logger.LogInformation("Graceful shutdown flush complete");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Graceful shutdown flush timed out after {Timeout}s — unflushed rows lost", timeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Graceful shutdown flush failed");
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task FlushAllOnce(CancellationToken ct)
    {
        foreach (var writer in _writers)
        {
            ct.ThrowIfCancellationRequested();
            await writer.FlushAllAsync(ct);
        }
    }
}
