using System.Net;
using AlgoTradeForge.HistoryLoader.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Composition;

public sealed class WebApiCompositionSmokeTests
{
    [Fact]
    public async Task Host_Composes_Starts_AndServesHealth()
    {
        var tempRoot = Directory.CreateTempSubdirectory("atf-composition-smoke-");
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseSetting("HistoryLoader:DataRoot", tempRoot.FullName);
                b.UseSetting("Storage:Local:DataRoot", tempRoot.FullName);
                b.UseDefaultServiceProvider(o => { o.ValidateOnBuild = true; o.ValidateScopes = true; });
                b.ConfigureTestServices(services =>
                    // Empty the symbol set BEFORE any hosted service reads CurrentValue —
                    // collectors idle instead of hitting live Binance from a unit test.
                    services.PostConfigure<HistoryLoaderOptions>(o => o.Assets.Clear()));
            });

            using var client = factory.CreateClient();   // builds + STARTS the host (hosted services construct here)
            var resp = await client.GetAsync("/health", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }
}
