using System.Net;
using System.Text;

namespace AlgoTradeForge.HistoryLoader.Tests.TestHelpers;

internal sealed class FakeHttpHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, Task<HttpResponseMessage>> Handler { get; set; } = _ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Handler(request);

    public static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}
