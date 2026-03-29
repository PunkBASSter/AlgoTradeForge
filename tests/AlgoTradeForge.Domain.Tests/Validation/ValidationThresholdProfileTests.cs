using AlgoTradeForge.Domain.Validation;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation;

public class ValidationThresholdProfileTests
{
    [Fact]
    public void CryptoStandard_HasDefaultValues()
    {
        var profile = ValidationThresholdProfile.CryptoStandard();

        Assert.Equal("Crypto-Standard", profile.Name);
        Assert.Equal(1.05, profile.BasicProfitability.MinProfitFactor);
        Assert.Equal(30, profile.BasicProfitability.MinTradeCount);
        Assert.Equal(40.0, profile.BasicProfitability.MaxDrawdownPct);
        Assert.Equal(0.05, profile.StatisticalSignificance.DsrPValue);
        Assert.Equal(0.95, profile.StatisticalSignificance.MinPsr);
    }

    [Fact]
    public void CryptoConservative_HasStricterThresholds()
    {
        var profile = ValidationThresholdProfile.CryptoConservative();

        Assert.Equal("Crypto-Conservative", profile.Name);
        Assert.Equal(1.10, profile.BasicProfitability.MinProfitFactor);
        Assert.Equal(50, profile.BasicProfitability.MinTradeCount);
        Assert.Equal(30.0, profile.BasicProfitability.MaxDrawdownPct);
        Assert.Equal(0.01, profile.StatisticalSignificance.DsrPValue);
        Assert.Equal(0.99, profile.StatisticalSignificance.MinPsr);
    }

    [Fact]
    public void GetByName_Standard_Resolves()
    {
        var profile = ValidationThresholdProfile.GetByName("Crypto-Standard");
        Assert.Equal("Crypto-Standard", profile.Name);
    }

    [Fact]
    public void GetByName_Conservative_Resolves()
    {
        var profile = ValidationThresholdProfile.GetByName("Crypto-Conservative");
        Assert.Equal("Crypto-Conservative", profile.Name);
    }

    [Fact]
    public void GetByName_Unknown_Throws()
    {
        Assert.Throws<ArgumentException>(() => ValidationThresholdProfile.GetByName("Unknown"));
    }
}
