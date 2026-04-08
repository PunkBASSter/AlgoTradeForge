using AlgoTradeForge.Domain.Validation;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation;

public class SubscriptionGroupHelperTests
{
    [Fact]
    public void PartitionIndices_NullMap_ReturnsSingleGroup()
    {
        var indices = new List<int> { 0, 1, 2 };

        var result = SubscriptionGroupHelper.PartitionIndices(indices, null);

        Assert.Single(result);
        Assert.True(result.ContainsKey("_all"));
        Assert.Equal(3, result["_all"].Count);
    }

    [Fact]
    public void PartitionIndices_MultipleGroups_PartitionsCorrectly()
    {
        var indices = new List<int> { 0, 1, 2, 3 };
        var groupMap = new Dictionary<int, string>
        {
            [0] = "BTCUSD:binance:1h",
            [1] = "BTCUSD:binance:1h",
            [2] = "ETHUSD:binance:1h",
            [3] = "ETHUSD:binance:1h",
        };

        var result = SubscriptionGroupHelper.PartitionIndices(indices, groupMap);

        Assert.Equal(2, result.Count);
        Assert.Equal([0, 1], result["BTCUSD:binance:1h"]);
        Assert.Equal([2, 3], result["ETHUSD:binance:1h"]);
    }

    [Fact]
    public void IsSingleGroup_NullMap_ReturnsTrue()
    {
        Assert.True(SubscriptionGroupHelper.IsSingleGroup(null));
    }

    [Fact]
    public void IsSingleGroup_OneDistinctGroup_ReturnsTrue()
    {
        var groupMap = new Dictionary<int, string>
        {
            [0] = "BTCUSD:binance:1h",
            [1] = "BTCUSD:binance:1h",
        };

        Assert.True(SubscriptionGroupHelper.IsSingleGroup(groupMap));
    }

    [Fact]
    public void IsSingleGroup_MultipleGroups_ReturnsFalse()
    {
        var groupMap = new Dictionary<int, string>
        {
            [0] = "BTCUSD:binance:1h",
            [1] = "ETHUSD:binance:1h",
        };

        Assert.False(SubscriptionGroupHelper.IsSingleGroup(groupMap));
    }

    [Fact]
    public void GetTrialIndicesForGroup_NullMap_ReturnsAllIndices()
    {
        var result = SubscriptionGroupHelper.GetTrialIndicesForGroup(null, "_all", 5);

        Assert.Equal(5, result.Count);
        Assert.Contains(0, result);
        Assert.Contains(4, result);
    }

    [Fact]
    public void GetTrialIndicesForGroup_ReturnsCorrectSubset()
    {
        var groupMap = new Dictionary<int, string>
        {
            [0] = "BTCUSD:binance:1h",
            [1] = "BTCUSD:binance:1h",
            [2] = "ETHUSD:binance:1h",
            [3] = "ETHUSD:binance:1h",
        };

        var btcTrials = SubscriptionGroupHelper.GetTrialIndicesForGroup(
            groupMap, "BTCUSD:binance:1h", 4);
        var ethTrials = SubscriptionGroupHelper.GetTrialIndicesForGroup(
            groupMap, "ETHUSD:binance:1h", 4);

        Assert.Equal(new HashSet<int> { 0, 1 }, btcTrials);
        Assert.Equal(new HashSet<int> { 2, 3 }, ethTrials);
    }
}
