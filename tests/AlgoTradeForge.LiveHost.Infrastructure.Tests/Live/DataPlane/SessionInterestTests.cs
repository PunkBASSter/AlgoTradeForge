using System.Threading.Channels;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.DataPlane;

public class SessionInterestTests
{
    private sealed class NoopStrategy : IInt64BarStrategy
    {
        public string Version => "test";
        public IList<DataFeedSubscription> DataSubscriptions { get; } = [];
        public void OnInit() { }
        public void OnTrade(Fill fill, Order order) { }
        public void OnBarStart(Int64Bar bar, DataFeedSubscription subscription) { }
        public void OnBarComplete(Int64Bar bar, DataFeedSubscription subscription) { }
    }
}
