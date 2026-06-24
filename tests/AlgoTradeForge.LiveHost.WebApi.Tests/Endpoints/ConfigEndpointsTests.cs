using System.Net;
using System.Net.Http.Json;
using AlgoTradeForge.LiveHost.Application.Collection;
using AlgoTradeForge.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AlgoTradeForge.LiveHost.WebApi.Tests;

public class ConfigEndpointsTests
{
    private static WebApplicationFactory<Program> FactoryWith(ICollectionConfigStore store) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll(typeof(ICollectionConfigStore));
                s.AddSingleton(store);
            }));

    [Fact]
    public async Task Get_returns_200_with_body_and_etag_header()
    {
        var store = Substitute.For<ICollectionConfigStore>();
        store.Load(Arg.Any<CancellationToken>())
            .Returns(new StoredCollectionConfig(new CollectionConfig([]), "etag-1"));
        using var client = FactoryWith(store).CreateClient();

        var resp = await client.GetAsync("/api/v1/config", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("\"etag-1\"", resp.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task Get_returns_200_without_etag_when_absent()
    {
        var store = Substitute.For<ICollectionConfigStore>();
        store.Load(Arg.Any<CancellationToken>())
            .Returns(new StoredCollectionConfig(new CollectionConfig([]), null));
        using var client = FactoryWith(store).CreateClient();

        var resp = await client.GetAsync("/api/v1/config", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Null(resp.Headers.ETag);
    }

    [Fact]
    public async Task Put_returns_409_on_stale_etag()
    {
        var store = Substitute.For<ICollectionConfigStore>();
        store.Load(Arg.Any<CancellationToken>())
            .Returns(new StoredCollectionConfig(new CollectionConfig([]), null));
        store.Save(Arg.Any<CollectionConfig>(), "stale", Arg.Any<CancellationToken>())
            .Throws(new ConcurrencyConflictException("collection.json", "stale", "current"));
        using var client = FactoryWith(store).CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Put, "/api/v1/config")
        {
            Content = JsonContent.Create(new CollectionConfig([])),
        };
        req.Headers.TryAddWithoutValidation("If-Match", "stale");

        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Put_returns_200_and_new_etag_on_success()
    {
        var store = Substitute.For<ICollectionConfigStore>();
        store.Load(Arg.Any<CancellationToken>())
            .Returns(new StoredCollectionConfig(new CollectionConfig([]), null));
        store.Save(Arg.Any<CollectionConfig>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("etag-2");
        using var client = FactoryWith(store).CreateClient();

        var resp = await client.PutAsJsonAsync("/api/v1/config", new CollectionConfig([]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("\"etag-2\"", resp.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task Put_strips_quotes_from_If_Match_before_passing_to_store()
    {
        var store = Substitute.For<ICollectionConfigStore>();
        store.Load(Arg.Any<CancellationToken>())
            .Returns(new StoredCollectionConfig(new CollectionConfig([]), null));
        store.Save(Arg.Any<CollectionConfig>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("etag-new");
        using var client = FactoryWith(store).CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Put, "/api/v1/config")
        {
            Content = JsonContent.Create(new CollectionConfig([])),
        };
        req.Headers.TryAddWithoutValidation("If-Match", "\"abc\"");

        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        await store.Received().Save(Arg.Any<CollectionConfig>(), "abc", Arg.Any<CancellationToken>());
    }
}
