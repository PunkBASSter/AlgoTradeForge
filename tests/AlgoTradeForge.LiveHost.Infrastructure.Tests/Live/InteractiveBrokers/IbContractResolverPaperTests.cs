using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

[Trait("Category", "IbPaper")]
public sealed class IbContractResolverPaperTests
{
    private static async Task<(IbConnection conn, IbContractResolver resolver)> ConnectAsync(CancellationToken ct)
    {
        var wrapper = new IbWrapper();
        var conn = new IbConnection(wrapper, IbPaperGatewayConfig.Options);
        await conn.Connect(ct: ct);
        var client = new IbConnectionContractDetailsClient(conn, wrapper, TimeProvider.System);
        return (conn, new IbContractResolver(client));
    }

    [Fact]
    public async Task Resolve_AaplStk_ReturnsConId()
    {
        if (!IbPaperGatewayConfig.IsConfigured) Assert.Skip(IbPaperGatewayConfig.SkipReason);

        var ct = TestContext.Current.CancellationToken;
        var (conn, resolver) = await ConnectAsync(ct);
        await using var _ = conn;

        var resolved = await resolver.Resolve(new IbContract("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD"), ct);

        Assert.True(resolved.ConId > 0);
        Assert.Equal("AAPL", resolved.LocalSymbol);
    }

    [Fact]
    public async Task Resolve_GoldFuture_ReturnsFrontMonthConId()
    {
        if (!IbPaperGatewayConfig.IsConfigured) Assert.Skip(IbPaperGatewayConfig.SkipReason);

        var ct = TestContext.Current.CancellationToken;
        var (conn, resolver) = await ConnectAsync(ct);
        await using var _ = conn;

        var resolved = await resolver.Resolve(new IbContract("GC", IbSecType.Fut, "COMEX", "", "USD"), ct);

        Assert.True(resolved.ConId > 0);
        Assert.False(string.IsNullOrEmpty(resolved.LastTradeDate)); // a concrete front-month expiry
    }
}
