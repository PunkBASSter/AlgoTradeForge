using System;
using System.Threading;
using System.Threading.Tasks;
using AlgoTradeForge.LiveHost.Infrastructure.Live;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public class ReconciliationShutdownClassificationTests
{
    [Fact]
    public void HttpTimeout_WithLiveToken_IsNotShutdown()
    {
        // Shape of what HttpClient.Timeout throws: TaskCanceledException(inner: TimeoutException),
        // no caller-token cancellation.
        var timeout = new TaskCanceledException("The request timed out.", new TimeoutException());
        using var cts = new CancellationTokenSource(); // live, never cancelled

        Assert.False(LiveSessionDispatcher.IsTrueShutdown(timeout, cts.Token));
    }

    [Fact]
    public void Oce_CarryingCancelledStoppingToken_IsShutdown()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var oce = new OperationCanceledException(cts.Token);

        Assert.True(LiveSessionDispatcher.IsTrueShutdown(oce, cts.Token));
    }

    [Fact]
    public void NonOce_IsNotShutdown()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // even with a cancelled token, a non-OCE is a real failure

        Assert.False(LiveSessionDispatcher.IsTrueShutdown(new InvalidOperationException("boom"), cts.Token));
    }
}
