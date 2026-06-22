using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.LiveHost.WebApi;

public sealed class RelayPumpHostedService(
    IVenueConnector connector,
    IOptions<RelayPumpOptions> opts,
    IFileStorage storage,
    IRelayTradeTap tap,
    TimeProvider time,
    ILogger<RelayPumpHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var o = opts.Value;
        if (o.Instruments.Length == 0)
        {
            logger.LogInformation("RelayPumpHostedService: no instruments configured, skipping relay pump.");
            return;
        }
        try
        {
            await RunPumpOnce(o.Instruments, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("RelayPumpHostedService shutting down.");
        }
    }

    internal async Task RunPumpOnce(IReadOnlyList<string> instruments, CancellationToken ct)
    {
        var o = opts.Value;
        var sink = new LocalSegmentSink(o.LocalRoot);
        var uploader = new SegmentUploader(storage, o.LocalRoot, o.KeyPrefix + "/" + connector.Venue);

        using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // writer must be disposed before the final SweepOnce so all segment files are closed.
        await using (var writer = new RelayWriter(connector.Venue, sink, new StreamPipelineOptions(), time, o.HeartbeatInterval))
        {
            var uploadLoop = UploadLoop(uploader, o.UploadInterval, uploadCts.Token);
            try
            {
                await RelayIngest.Pump(connector, writer, instruments, tap: tap, ct: ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // normal shutdown — fall through to final flush
            }
            finally
            {
                await uploadCts.CancelAsync().ConfigureAwait(false);
            }

            await uploadLoop.ConfigureAwait(false);
        } // writer.DisposeAsync() here: flushes + closes all segment files

        await uploader.SweepOnce(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task UploadLoop(SegmentUploader uploader, TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await uploader.SweepOnce(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
