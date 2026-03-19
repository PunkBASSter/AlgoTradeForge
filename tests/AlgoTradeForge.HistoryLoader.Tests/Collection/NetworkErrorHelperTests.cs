using System.Net;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

public sealed class NetworkErrorHelperTests
{
    [Fact]
    public void HttpRequestException_NullStatusCode_IsNetworkError()
    {
        var ex = new HttpRequestException("Name resolution failed");

        Assert.True(NetworkErrorHelper.IsNetworkError(ex));
    }

    [Fact]
    public void HttpRequestException_WithStatusCode_IsNotNetworkError()
    {
        var ex = new HttpRequestException("Bad Request", null, HttpStatusCode.BadRequest);

        Assert.False(NetworkErrorHelper.IsNetworkError(ex));
    }

    [Fact]
    public void HttpRequestException_418StatusCode_IsNotNetworkError()
    {
        var ex = new HttpRequestException("Teapot", null, (HttpStatusCode)418);

        Assert.False(NetworkErrorHelper.IsNetworkError(ex));
    }

    [Fact]
    public void TaskCanceledException_WithTimeoutInner_IsNetworkError()
    {
        var inner = new TimeoutException("Request timed out");
        var ex = new TaskCanceledException("Timeout", inner);

        Assert.True(NetworkErrorHelper.IsNetworkError(ex));
    }

    [Fact]
    public void TaskCanceledException_WithoutTimeoutInner_IsNotNetworkError()
    {
        var ex = new TaskCanceledException("Cancelled by user");

        Assert.False(NetworkErrorHelper.IsNetworkError(ex));
    }

    [Fact]
    public void GenericException_IsNotNetworkError()
    {
        var ex = new InvalidOperationException("Something else");

        Assert.False(NetworkErrorHelper.IsNetworkError(ex));
    }

    [Fact]
    public void TaskCanceledException_WithNonTimeoutInner_IsNotNetworkError()
    {
        var inner = new InvalidOperationException("Not a timeout");
        var ex = new TaskCanceledException("Cancelled", inner);

        Assert.False(NetworkErrorHelper.IsNetworkError(ex));
    }
}
