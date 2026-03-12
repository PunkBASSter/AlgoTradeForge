using AlgoTradeForge.Domain.History;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.History;

public class DataFeedSchemaTests
{
    [Fact]
    public void GetColumnIndex_ExistingColumn_ReturnsCorrectIndex()
    {
        var schema = new DataFeedSchema("funding", ["fundingRate", "markPrice"]);

        Assert.Equal(0, schema.GetColumnIndex("fundingRate"));
        Assert.Equal(1, schema.GetColumnIndex("markPrice"));
    }

    [Fact]
    public void GetColumnIndex_NonExistentColumn_Throws()
    {
        var schema = new DataFeedSchema("funding", ["fundingRate", "markPrice"]);

        var ex = Assert.Throws<ArgumentException>(() => schema.GetColumnIndex("volume"));
        Assert.Contains("volume", ex.Message);
        Assert.Contains("funding", ex.Message);
    }

    [Fact]
    public void ColumnCount_ReturnsNumberOfColumns()
    {
        var schema = new DataFeedSchema("oi", ["sumOI", "sumOI_USD"]);

        Assert.Equal(2, schema.ColumnCount);
    }

    [Fact]
    public void AutoApply_NullByDefault()
    {
        var schema = new DataFeedSchema("oi", ["sumOI"]);

        Assert.Null(schema.AutoApply);
    }

    [Fact]
    public void AutoApply_WhenProvided_IsAccessible()
    {
        var autoApply = new AutoApplyConfig(AutoApplyType.FundingRate, "fundingRate");
        var schema = new DataFeedSchema("funding", ["fundingRate", "markPrice"], autoApply);

        Assert.NotNull(schema.AutoApply);
        Assert.Equal(AutoApplyType.FundingRate, schema.AutoApply.Type);
        Assert.Equal("fundingRate", schema.AutoApply.RateColumn);
    }
}
