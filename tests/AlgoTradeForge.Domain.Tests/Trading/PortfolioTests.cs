using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Trading;

public class PortfolioTests
{
    [Fact]
    public void Initialize_SetsCashAndClearsPositions()
    {
        var portfolio = new Portfolio { InitialCash = 100_000L };
        portfolio.GetOrCreatePosition(TestAssets.Aapl);
        portfolio.Initialize();

        Assert.Equal(100_000L, portfolio.Cash);
        Assert.Empty(portfolio.Positions);
    }

    [Fact]
    public void GetPosition_NonExistent_ReturnsNull()
    {
        var portfolio = new Portfolio { InitialCash = 100_000L };
        portfolio.Initialize();

        Assert.Null(portfolio.GetPosition("AAPL"));
    }

    [Fact]
    public void GetPosition_Existing_ReturnsPosition()
    {
        var portfolio = new Portfolio { InitialCash = 100_000L };
        portfolio.Initialize();
        var created = portfolio.GetOrCreatePosition(TestAssets.Aapl);

        var found = portfolio.GetPosition("AAPL");

        Assert.Same(created, found);
    }

    [Fact]
    public void GetOrCreatePosition_NewAsset_CreatesPosition()
    {
        var portfolio = new Portfolio { InitialCash = 100_000L };
        portfolio.Initialize();

        var position = portfolio.GetOrCreatePosition(TestAssets.Aapl);

        Assert.NotNull(position);
        Assert.Equal(TestAssets.Aapl, position.Asset);
        Assert.Equal(0m, position.Quantity);
        Assert.True(portfolio.Positions.ContainsKey("AAPL"));
    }

    [Fact]
    public void GetOrCreatePosition_ExistingAsset_ReturnsSamePosition()
    {
        var portfolio = new Portfolio { InitialCash = 100_000L };
        portfolio.Initialize();
        var first = portfolio.GetOrCreatePosition(TestAssets.Aapl);

        var second = portfolio.GetOrCreatePosition(TestAssets.Aapl);

        Assert.Same(first, second);
    }

    [Fact]
    public void Equity_SinglePrice_CashOnly_ReturnsCash()
    {
        var portfolio = new Portfolio { InitialCash = 100_000L };
        portfolio.Initialize();

        Assert.Equal(100_000L, portfolio.Equity(150L));
    }

    [Fact]
    public void Equity_SinglePrice_WithEquityPosition_CalculatesCorrectly()
    {
        var portfolio = new Portfolio { InitialCash = 100_000L };
        portfolio.Initialize();
        var fill = TestFills.BuyAapl(150L, 100m);
        portfolio.Apply(fill);

        // Cash: 100000 - 150*100*1 = 85000
        // Position value: 100 * 160 * 1 = 16000
        // Total: 101000
        Assert.Equal(101_000L, portfolio.Equity(160L));
    }

    [Fact]
    public void Equity_SinglePrice_WithFuturesPosition_AppliesMultiplier()
    {
        var portfolio = new Portfolio { InitialCash = 100_000L };
        portfolio.Initialize();
        var fill = TestFills.BuyEs(5000L, 2m);
        portfolio.Apply(fill);

        // Cash: 100000 - 5000*2*50 = -400000 (this demonstrates margin trading)
        // Position value: 2 * 5010 * 50 = 501000
        // Total: 101000
        Assert.Equal(101_000L, portfolio.Equity(5010L));
    }

    [Fact]
    public void Equity_PriceDict_MultiplePositions_CalculatesCorrectly()
    {
        var portfolio = new Portfolio { InitialCash = 100_000L };
        portfolio.Initialize();
        portfolio.Apply(TestFills.BuyAapl(150L, 100m));
        portfolio.Apply(TestFills.Buy(TestAssets.Msft, 300L, 50m));

        // Cash: 100000 - 15000 - 15000 = 70000
        var prices = new Dictionary<string, long>
        {
            ["AAPL"] = 160L,
            ["MSFT"] = 320L
        };

        // Position value: 100*160 + 50*320 = 16000 + 16000 = 32000
        // Total: 70000 + 32000 = 102000
        Assert.Equal(102_000L, portfolio.Equity(prices));
    }

    [Fact]
    public void Equity_PriceDict_MissingPrice_ExcludesFromValue()
    {
        var portfolio = new Portfolio { InitialCash = 100_000L };
        portfolio.Initialize();
        portfolio.Apply(TestFills.BuyAapl(150L, 100m));
        portfolio.Apply(TestFills.Buy(TestAssets.Msft, 300L, 50m));

        // Only AAPL price provided
        var prices = new Dictionary<string, long> { ["AAPL"] = 160L };

        // Cash: 70000, AAPL: 16000, MSFT: excluded
        // Total: 86000
        Assert.Equal(86_000L, portfolio.Equity(prices));
    }

    [Fact]
    public void Apply_BuyOrder_DeductsCash()
    {
        var portfolio = new Portfolio { InitialCash = 100_000L };
        portfolio.Initialize();

        portfolio.Apply(TestFills.BuyAapl(150L, 100m));

        // 100000 - 150*100 = 85000
        Assert.Equal(85_000L, portfolio.Cash);
    }

    [Fact]
    public void Apply_SellOrder_AddsCash()
    {
        var portfolio = new Portfolio { InitialCash = 100_000L };
        portfolio.Initialize();
        // First buy to have position
        portfolio.Apply(TestFills.BuyAapl(150L, 100m));

        portfolio.Apply(TestFills.SellAapl(160L, 100m));

        // 85000 + 160*100 = 101000
        Assert.Equal(101_000L, portfolio.Cash);
    }

    [Fact]
    public void Apply_WithCommission_DeductsFromCash()
    {
        var portfolio = new Portfolio { InitialCash = 100_000L };
        portfolio.Initialize();

        portfolio.Apply(TestFills.BuyAapl(150L, 100m, commission: 5L));

        // 100000 - 150*100 - 5 = 84995
        Assert.Equal(84_995L, portfolio.Cash);
    }

    [Fact]
    public void Apply_UpdatesPosition()
    {
        var portfolio = new Portfolio { InitialCash = 100_000L };
        portfolio.Initialize();

        portfolio.Apply(TestFills.BuyAapl(150L, 100m));

        var position = portfolio.GetPosition("AAPL");
        Assert.NotNull(position);
        Assert.Equal(100m, position.Quantity);
        Assert.Equal(150L, position.AverageEntryPrice);
    }

    [Fact]
    public void Apply_FuturesBuy_AppliesMultiplierToCash()
    {
        var portfolio = new Portfolio { InitialCash = 500_000L };
        portfolio.Initialize();

        portfolio.Apply(TestFills.BuyEs(5000L, 2m));

        // 500000 - 5000*2*50 = 0
        Assert.Equal(0L, portfolio.Cash);
    }
}
