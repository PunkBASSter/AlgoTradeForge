using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using Xunit;

namespace AlgoTradeForge.LiveHost.Application.Tests.Live.Recovery;

public class RecoveryVocabularyTests
{
    [Fact]
    public void Discontinuity_carries_time_window_and_reason()
    {
        var d = new Discontinuity(FromTs: 1000, ToTs: 2000, DiscontinuityReason.Disconnect);
        Assert.Equal(1000, d.FromTs);
        Assert.Equal(2000, d.ToTs);
        Assert.Equal(DiscontinuityReason.Disconnect, d.Reason);
    }

    [Fact]
    public void NoBackfill_policy_has_zero_budget()
    {
        Assert.Equal(TimeSpan.Zero, RecoveryPolicy.NoBackfill.BackfillBudget);
    }
}
