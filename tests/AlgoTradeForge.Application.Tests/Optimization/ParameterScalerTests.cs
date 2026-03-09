using System.Text.Json;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Optimization.Space;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Optimization;

public class ParameterScalerTests
{
    private readonly IOptimizationSpaceProvider _spaceProvider = Substitute.For<IOptimizationSpaceProvider>();

    private static OptimizationSpaceDescriptor MakeDescriptor(params ParameterAxis[] axes) =>
        new("TestStrategy", typeof(object), typeof(object), axes);

    [Fact]
    public void QuoteAssetParam_IsScaledByAmountToTicks()
    {
        // tickSize=0.01 → 100 * 100 = 10000L
        var axis = new NumericRangeAxis("MinThreshold", 50, 500, 50, typeof(long), ParamUnit.QuoteAsset);
        _spaceProvider.GetDescriptor("TestStrategy").Returns(MakeDescriptor(axis));
        var scale = new ScaleContext(0.01m);

        var parameters = new Dictionary<string, object> { ["MinThreshold"] = 100m };

        var result = ParameterScaler.ScaleQuoteAssetParams(
            _spaceProvider, "TestStrategy", parameters, scale);

        Assert.NotNull(result);
        Assert.Equal(10000L, result["MinThreshold"]);
    }

    [Fact]
    public void RawParam_IsNotScaled()
    {
        var quoteAxis = new NumericRangeAxis("MinThreshold", 50, 500, 50, typeof(long), ParamUnit.QuoteAsset);
        var rawAxis = new NumericRangeAxis("Period", 5, 50, 1, typeof(int), ParamUnit.Raw);
        _spaceProvider.GetDescriptor("TestStrategy").Returns(MakeDescriptor(quoteAxis, rawAxis));
        var scale = new ScaleContext(0.01m);

        var parameters = new Dictionary<string, object>
        {
            ["MinThreshold"] = 100m,
            ["Period"] = 14
        };

        var result = ParameterScaler.ScaleQuoteAssetParams(
            _spaceProvider, "TestStrategy", parameters, scale);

        Assert.NotNull(result);
        Assert.Equal(10000L, result["MinThreshold"]);
        Assert.Equal(14, result["Period"]); // untouched
    }

    [Fact]
    public void NullParameters_ReturnsNull()
    {
        var result = ParameterScaler.ScaleQuoteAssetParams(
            _spaceProvider, "TestStrategy", null, new ScaleContext(0.01m));

        Assert.Null(result);
    }

    [Fact]
    public void EmptyParameters_ReturnsEmpty()
    {
        var parameters = new Dictionary<string, object>();

        var result = ParameterScaler.ScaleQuoteAssetParams(
            _spaceProvider, "TestStrategy", parameters, new ScaleContext(0.01m));

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void NullDescriptor_ReturnsOriginalParameters()
    {
        _spaceProvider.GetDescriptor("Unknown").Returns((IOptimizationSpaceDescriptor?)null);

        var parameters = new Dictionary<string, object> { ["Foo"] = 42m };

        var result = ParameterScaler.ScaleQuoteAssetParams(
            _spaceProvider, "Unknown", parameters, new ScaleContext(0.01m));

        Assert.Same(parameters, result);
    }

    [Fact]
    public void JsonElement_IsHandledCorrectly()
    {
        var axis = new NumericRangeAxis("MinThreshold", 50, 500, 50, typeof(long), ParamUnit.QuoteAsset);
        _spaceProvider.GetDescriptor("TestStrategy").Returns(MakeDescriptor(axis));
        var scale = new ScaleContext(0.01m);

        // Simulate JSON deserialization (API request path)
        var json = JsonSerializer.Deserialize<JsonElement>("200");
        var parameters = new Dictionary<string, object> { ["MinThreshold"] = json };

        var result = ParameterScaler.ScaleQuoteAssetParams(
            _spaceProvider, "TestStrategy", parameters, scale);

        Assert.NotNull(result);
        Assert.Equal(20000L, result["MinThreshold"]);
    }

    [Fact]
    public void MissingParam_IsSkipped()
    {
        // Axis exists in descriptor but parameter not provided — should not throw
        var axis = new NumericRangeAxis("MinThreshold", 50, 500, 50, typeof(long), ParamUnit.QuoteAsset);
        _spaceProvider.GetDescriptor("TestStrategy").Returns(MakeDescriptor(axis));
        var scale = new ScaleContext(0.01m);

        var parameters = new Dictionary<string, object> { ["OtherParam"] = 42 };

        var result = ParameterScaler.ScaleQuoteAssetParams(
            _spaceProvider, "TestStrategy", parameters, scale);

        Assert.NotNull(result);
        Assert.Equal(42, result["OtherParam"]);
        Assert.False(result.ContainsKey("MinThreshold"));
    }

    [Fact]
    public void OriginalDictionary_IsNotMutated()
    {
        var axis = new NumericRangeAxis("MinThreshold", 50, 500, 50, typeof(long), ParamUnit.QuoteAsset);
        _spaceProvider.GetDescriptor("TestStrategy").Returns(MakeDescriptor(axis));
        var scale = new ScaleContext(0.01m);

        var original = new Dictionary<string, object> { ["MinThreshold"] = 100m };

        ParameterScaler.ScaleQuoteAssetParams(
            _spaceProvider, "TestStrategy", original, scale);

        Assert.Equal(100m, original["MinThreshold"]); // original untouched
    }

    // --- Module sub-parameter scaling ---

    [Fact]
    public void ModuleSubParam_QuoteAsset_IsScaled()
    {
        // Module variant "AtrFilter" has a QuoteAsset sub-param "MinAtr"
        var subAxis = new NumericRangeAxis("MinAtr", 0, 50, 0.5m, typeof(long), ParamUnit.QuoteAsset);
        var variant = new ModuleVariantDescriptor("AtrFilter", typeof(object), typeof(object), [subAxis]);
        var moduleAxis = new ModuleSlotAxis("Filter", typeof(object), [variant]);
        _spaceProvider.GetDescriptor("TestStrategy").Returns(MakeDescriptor(moduleAxis));
        var scale = new ScaleContext(0.01m);

        var parameters = new Dictionary<string, object>
        {
            ["Filter"] = new ModuleSelection("AtrFilter", new Dictionary<string, object>
            {
                ["MinAtr"] = 10m
            })
        };

        var result = ParameterScaler.ScaleQuoteAssetParams(
            _spaceProvider, "TestStrategy", parameters, scale);

        Assert.NotNull(result);
        var selection = Assert.IsType<ModuleSelection>(result["Filter"]);
        Assert.Equal("AtrFilter", selection.TypeKey);
        Assert.Equal(1000L, selection.Params["MinAtr"]); // 10 * 100 = 1000
    }

    [Fact]
    public void ModuleSubParam_Raw_IsNotScaled()
    {
        var quoteAxis = new NumericRangeAxis("MinAtr", 0, 50, 0.5m, typeof(long), ParamUnit.QuoteAsset);
        var rawAxis = new NumericRangeAxis("Period", 5, 50, 1, typeof(int), ParamUnit.Raw);
        var variant = new ModuleVariantDescriptor("AtrFilter", typeof(object), typeof(object), [quoteAxis, rawAxis]);
        var moduleAxis = new ModuleSlotAxis("Filter", typeof(object), [variant]);
        _spaceProvider.GetDescriptor("TestStrategy").Returns(MakeDescriptor(moduleAxis));
        var scale = new ScaleContext(0.01m);

        var parameters = new Dictionary<string, object>
        {
            ["Filter"] = new ModuleSelection("AtrFilter", new Dictionary<string, object>
            {
                ["MinAtr"] = 10m,
                ["Period"] = 14
            })
        };

        var result = ParameterScaler.ScaleQuoteAssetParams(
            _spaceProvider, "TestStrategy", parameters, scale);

        Assert.NotNull(result);
        var selection = Assert.IsType<ModuleSelection>(result["Filter"]);
        Assert.Equal(1000L, selection.Params["MinAtr"]);
        Assert.Equal(14, selection.Params["Period"]); // untouched
    }

    [Fact]
    public void ModuleSelection_WithUnknownVariant_IsNotScaled()
    {
        // If the ModuleSelection references a variant not in the descriptor, skip gracefully
        var subAxis = new NumericRangeAxis("MinAtr", 0, 50, 0.5m, typeof(long), ParamUnit.QuoteAsset);
        var variant = new ModuleVariantDescriptor("AtrFilter", typeof(object), typeof(object), [subAxis]);
        var moduleAxis = new ModuleSlotAxis("Filter", typeof(object), [variant]);
        _spaceProvider.GetDescriptor("TestStrategy").Returns(MakeDescriptor(moduleAxis));
        var scale = new ScaleContext(0.01m);

        var parameters = new Dictionary<string, object>
        {
            ["Filter"] = new ModuleSelection("UnknownFilter", new Dictionary<string, object>
            {
                ["MinAtr"] = 10m
            })
        };

        var result = ParameterScaler.ScaleQuoteAssetParams(
            _spaceProvider, "TestStrategy", parameters, scale);

        Assert.NotNull(result);
        var selection = Assert.IsType<ModuleSelection>(result["Filter"]);
        Assert.Equal(10m, selection.Params["MinAtr"]); // not scaled — variant not found
    }

    [Fact]
    public void ModuleSelection_WithEmptyParams_IsNotModified()
    {
        var subAxis = new NumericRangeAxis("MinAtr", 0, 50, 0.5m, typeof(long), ParamUnit.QuoteAsset);
        var variant = new ModuleVariantDescriptor("AtrFilter", typeof(object), typeof(object), [subAxis]);
        var moduleAxis = new ModuleSlotAxis("Filter", typeof(object), [variant]);
        _spaceProvider.GetDescriptor("TestStrategy").Returns(MakeDescriptor(moduleAxis));
        var scale = new ScaleContext(0.01m);

        var parameters = new Dictionary<string, object>
        {
            ["Filter"] = new ModuleSelection("AtrFilter", new Dictionary<string, object>())
        };

        var result = ParameterScaler.ScaleQuoteAssetParams(
            _spaceProvider, "TestStrategy", parameters, scale);

        Assert.NotNull(result);
        var selection = Assert.IsType<ModuleSelection>(result["Filter"]);
        Assert.Empty(selection.Params);
    }

    [Fact]
    public void TopLevelAndModuleSubParams_BothScaled()
    {
        // Strategy has both a top-level QuoteAsset param and a module with QuoteAsset sub-param
        var topAxis = new NumericRangeAxis("MinThreshold", 50, 500, 50, typeof(long), ParamUnit.QuoteAsset);
        var subAxis = new NumericRangeAxis("MinAtr", 0, 50, 0.5m, typeof(long), ParamUnit.QuoteAsset);
        var variant = new ModuleVariantDescriptor("AtrFilter", typeof(object), typeof(object), [subAxis]);
        var moduleAxis = new ModuleSlotAxis("Filter", typeof(object), [variant]);
        _spaceProvider.GetDescriptor("TestStrategy").Returns(MakeDescriptor(topAxis, moduleAxis));
        var scale = new ScaleContext(0.01m);

        var parameters = new Dictionary<string, object>
        {
            ["MinThreshold"] = 100m,
            ["Filter"] = new ModuleSelection("AtrFilter", new Dictionary<string, object>
            {
                ["MinAtr"] = 5m
            })
        };

        var result = ParameterScaler.ScaleQuoteAssetParams(
            _spaceProvider, "TestStrategy", parameters, scale);

        Assert.NotNull(result);
        Assert.Equal(10000L, result["MinThreshold"]);
        var selection = Assert.IsType<ModuleSelection>(result["Filter"]);
        Assert.Equal(500L, selection.Params["MinAtr"]);
    }

    [Fact]
    public void NonModuleSelectionValue_InModuleSlot_IsIgnored()
    {
        // If the dict value for a module slot isn't a ModuleSelection, skip it
        var subAxis = new NumericRangeAxis("MinAtr", 0, 50, 0.5m, typeof(long), ParamUnit.QuoteAsset);
        var variant = new ModuleVariantDescriptor("AtrFilter", typeof(object), typeof(object), [subAxis]);
        var moduleAxis = new ModuleSlotAxis("Filter", typeof(object), [variant]);
        _spaceProvider.GetDescriptor("TestStrategy").Returns(MakeDescriptor(moduleAxis));
        var scale = new ScaleContext(0.01m);

        var parameters = new Dictionary<string, object>
        {
            ["Filter"] = "not-a-module-selection"
        };

        var result = ParameterScaler.ScaleQuoteAssetParams(
            _spaceProvider, "TestStrategy", parameters, scale);

        Assert.NotNull(result);
        Assert.Equal("not-a-module-selection", result["Filter"]); // untouched
    }

    [Fact]
    public void OriginalModuleSelection_IsNotMutated()
    {
        var subAxis = new NumericRangeAxis("MinAtr", 0, 50, 0.5m, typeof(long), ParamUnit.QuoteAsset);
        var variant = new ModuleVariantDescriptor("AtrFilter", typeof(object), typeof(object), [subAxis]);
        var moduleAxis = new ModuleSlotAxis("Filter", typeof(object), [variant]);
        _spaceProvider.GetDescriptor("TestStrategy").Returns(MakeDescriptor(moduleAxis));
        var scale = new ScaleContext(0.01m);

        var originalSubParams = new Dictionary<string, object> { ["MinAtr"] = 10m };
        var originalSelection = new ModuleSelection("AtrFilter", originalSubParams);
        var original = new Dictionary<string, object> { ["Filter"] = originalSelection };

        ParameterScaler.ScaleQuoteAssetParams(
            _spaceProvider, "TestStrategy", original, scale);

        // The original top-level dict IS mutated (by design — ScaleAxes works on the clone
        // created in ScaleQuoteAssetParams), but the original ModuleSelection's Params are not.
        Assert.Equal(10m, originalSubParams["MinAtr"]);
    }
}
