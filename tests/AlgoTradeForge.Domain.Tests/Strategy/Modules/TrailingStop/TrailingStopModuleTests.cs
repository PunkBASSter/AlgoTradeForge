using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Modules.TrailingStop;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Modules.TrailingStop;

public sealed class TrailingStopModuleTests
{
    private static TrailingStopModule CreateModule(double atrMultiplier = 2.0) =>
        new(new TrailingStopParams { Variant = TrailingStopVariant.Atr, AtrMultiplier = atrMultiplier });

    [Fact]
    public void Activate_CreatesState_GetCurrentStopReturnsInitialStop()
    {
        var module = CreateModule();
        module.Activate(1, entryPrice: 50000, OrderSide.Buy, initialStop: 48000);

        Assert.Equal(48000L, module.GetCurrentStop(1));
    }

    [Fact]
    public void Update_LongPosition_RatchetsStopUp()
    {
        var module = CreateModule(atrMultiplier: 2.0);
        module.Activate(1, entryPrice: 50000, OrderSide.Buy, initialStop: 48000);

        // Price moves up: high = 52000, ATR = 500
        // New high water mark = 52000, stop = 52000 - 2*500 = 51000 > 48000 → ratchets
        var bar = TestBars.Create(51000, 52000, 50500, 51500);
        var newStop = module.Update(1, bar, currentAtr: 500);

        Assert.NotNull(newStop);
        Assert.Equal(51000L, newStop.Value);
    }

    [Fact]
    public void Update_LongPosition_StopNeverMovesDown()
    {
        var module = CreateModule(atrMultiplier: 2.0);
        module.Activate(1, entryPrice: 50000, OrderSide.Buy, initialStop: 48000);

        // Move up first
        var bar1 = TestBars.Create(51000, 52000, 50500, 51500);
        module.Update(1, bar1, currentAtr: 500);
        var stopAfterUp = module.GetCurrentStop(1);

        // Price moves back down — stop should NOT move down
        var bar2 = TestBars.Create(50000, 50500, 49000, 49500);
        module.Update(1, bar2, currentAtr: 500);
        var stopAfterDown = module.GetCurrentStop(1);

        Assert.Equal(stopAfterUp, stopAfterDown);
    }

    [Fact]
    public void Update_ShortPosition_RatchetsStopDown()
    {
        var module = CreateModule(atrMultiplier: 2.0);
        module.Activate(1, entryPrice: 50000, OrderSide.Sell, initialStop: 52000);

        // Price moves down: low = 48000, ATR = 500
        // New low water mark = 48000, stop = 48000 + 2*500 = 49000 < 52000 → ratchets
        var bar = TestBars.Create(49000, 49500, 48000, 48500);
        var newStop = module.Update(1, bar, currentAtr: 500);

        Assert.NotNull(newStop);
        Assert.True(newStop.Value < 52000L, $"Short stop should ratchet down, got {newStop.Value}");
    }

    [Fact]
    public void Update_ShortPosition_StopNeverMovesUp()
    {
        var module = CreateModule(atrMultiplier: 2.0);
        module.Activate(1, entryPrice: 50000, OrderSide.Sell, initialStop: 52000);

        // Move down first
        var bar1 = TestBars.Create(49000, 49500, 48000, 48500);
        module.Update(1, bar1, currentAtr: 500);
        var stopAfterDown = module.GetCurrentStop(1);

        // Price moves back up — stop should NOT move up
        var bar2 = TestBars.Create(51000, 51500, 50000, 51000);
        module.Update(1, bar2, currentAtr: 500);
        var stopAfterUp = module.GetCurrentStop(1);

        Assert.True(stopAfterUp <= stopAfterDown!.Value,
            $"Short stop should never move up: after down={stopAfterDown}, after up={stopAfterUp}");
    }

    [Fact]
    public void Update_UnknownGroup_ReturnsNull()
    {
        var module = CreateModule();
        var bar = TestBars.Flat();

        Assert.Null(module.Update(999, bar));
    }

    [Fact]
    public void GetCurrentStop_UnknownGroup_ReturnsNull()
    {
        var module = CreateModule();
        Assert.Null(module.GetCurrentStop(999));
    }

    [Fact]
    public void Remove_CleansUpState()
    {
        var module = CreateModule();
        module.Activate(1, 50000, OrderSide.Buy, 48000);

        module.Remove(1);

        Assert.Null(module.GetCurrentStop(1));
    }

    [Fact]
    public void MultipleConcurrentGroups_TrackedIndependently()
    {
        var module = CreateModule(atrMultiplier: 2.0);
        module.Activate(1, 50000, OrderSide.Buy, 48000);
        module.Activate(2, 60000, OrderSide.Sell, 62000);

        Assert.Equal(48000L, module.GetCurrentStop(1));
        Assert.Equal(62000L, module.GetCurrentStop(2));

        // Only group 1 should move
        var bar = TestBars.Create(51000, 52000, 50500, 51500);
        module.Update(1, bar, currentAtr: 500);

        Assert.NotEqual(48000L, module.GetCurrentStop(1)); // ratcheted
        Assert.Equal(62000L, module.GetCurrentStop(2));    // unchanged
    }

    [Fact]
    public void Update_NoAtr_FallsBackToPercentOfPrice()
    {
        var module = CreateModule(atrMultiplier: 2.0);
        module.Activate(1, 50000, OrderSide.Buy, 48000);

        // No ATR provided → fallback = waterMark / 50
        var bar = TestBars.Create(52000, 54000, 51000, 53000);
        var newStop = module.Update(1, bar, currentAtr: 0);

        // High water mark = 54000, atr fallback = 54000/50 = 1080
        // stop = 54000 - 2*1080 = 51840 > 48000 → should ratchet
        Assert.NotNull(newStop);
        Assert.True(newStop.Value > 48000L);
    }

    [Fact]
    public void Update_ReturnsNull_WhenStopUnchanged()
    {
        var module = CreateModule(atrMultiplier: 2.0);
        module.Activate(1, 50000, OrderSide.Buy, 48000);

        // Small bar doesn't move high water mark enough to ratchet
        var bar = TestBars.Create(50000, 50010, 49990, 50000);
        // HWM = max(50000, 50010) = 50010
        // stop = 50010 - 2*500 = 49010 > 48000 → ratchets first time
        module.Update(1, bar, currentAtr: 500);

        // Same bar again — stop shouldn't change
        var result = module.Update(1, bar, currentAtr: 500);
        Assert.Null(result);
    }
}
