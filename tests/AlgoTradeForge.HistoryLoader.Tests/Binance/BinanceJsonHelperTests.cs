using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Infrastructure.Binance;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Binance;

public sealed class BinanceJsonHelperTests
{
    // -------------------------------------------------------------------------
    // TryParseDouble (named property overload)
    // -------------------------------------------------------------------------

    [Fact]
    public void TryParseDouble_ValidNumber_ReturnsTrueAndParsesValue()
    {
        var element = ParseSingle("""{"rate":"0.0001"}""");
        Assert.True(BinanceJsonHelper.TryParseDouble(element, "rate", out var result));
        Assert.Equal(0.0001, result, precision: 10);
    }

    [Fact]
    public void TryParseDouble_NegativeNumber_ReturnsTrueAndParsesValue()
    {
        var element = ParseSingle("""{"rate":"-0.0002"}""");
        Assert.True(BinanceJsonHelper.TryParseDouble(element, "rate", out var result));
        Assert.Equal(-0.0002, result, precision: 10);
    }

    [Fact]
    public void TryParseDouble_ScientificNotation_ReturnsTrueAndParsesValue()
    {
        var element = ParseSingle("""{"rate":"1.5e-4"}""");
        Assert.True(BinanceJsonHelper.TryParseDouble(element, "rate", out var result));
        Assert.Equal(0.00015, result, precision: 10);
    }

    [Fact]
    public void TryParseDouble_EmptyString_ReturnsFalse()
    {
        var element = ParseSingle("""{"rate":""}""");
        Assert.False(BinanceJsonHelper.TryParseDouble(element, "rate", out var result));
        Assert.Equal(0, result);
    }

    [Fact]
    public void TryParseDouble_NullValue_ReturnsFalse()
    {
        var element = ParseSingle("""{"rate":null}""");
        Assert.False(BinanceJsonHelper.TryParseDouble(element, "rate", out var result));
        Assert.Equal(0, result);
    }

    [Fact]
    public void TryParseDouble_NonNumericString_ReturnsFalse()
    {
        var element = ParseSingle("""{"rate":"abc"}""");
        Assert.False(BinanceJsonHelper.TryParseDouble(element, "rate", out var result));
        Assert.Equal(0, result);
    }

    // -------------------------------------------------------------------------
    // TryParseDouble (array index overload)
    // -------------------------------------------------------------------------

    [Fact]
    public void TryParseDouble_ArrayIndex_ValidNumber_ReturnsTrueAndParsesValue()
    {
        using var doc = JsonDocument.Parse("""["50000.5"]""");
        var arrayElement = doc.RootElement.EnumerateArray().First();
        Assert.True(BinanceJsonHelper.TryParseDouble(arrayElement, out var result));
        Assert.Equal(50000.5, result, precision: 10);
    }

    [Fact]
    public void TryParseDouble_ArrayIndex_EmptyString_ReturnsFalse()
    {
        using var doc = JsonDocument.Parse("""[""]""");
        var arrayElement = doc.RootElement.EnumerateArray().First();
        Assert.False(BinanceJsonHelper.TryParseDouble(arrayElement, out _));
    }

    // -------------------------------------------------------------------------
    // TryParseDecimal (array index overload)
    // -------------------------------------------------------------------------

    [Fact]
    public void TryParseDecimal_ValidNumber_ReturnsTrueAndParsesValue()
    {
        using var doc = JsonDocument.Parse("""["50000.12345678"]""");
        var arrayElement = doc.RootElement.EnumerateArray().First();
        Assert.True(BinanceJsonHelper.TryParseDecimal(arrayElement, out var result));
        Assert.Equal(50000.12345678m, result);
    }

    [Fact]
    public void TryParseDecimal_EmptyString_ReturnsFalse()
    {
        using var doc = JsonDocument.Parse("""[""]""");
        var arrayElement = doc.RootElement.EnumerateArray().First();
        Assert.False(BinanceJsonHelper.TryParseDecimal(arrayElement, out _));
    }

    [Fact]
    public void TryParseDecimal_NullValue_ReturnsFalse()
    {
        using var doc = JsonDocument.Parse("""[null]""");
        var arrayElement = doc.RootElement.EnumerateArray().First();
        Assert.False(BinanceJsonHelper.TryParseDecimal(arrayElement, out _));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static JsonElement ParseSingle(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
