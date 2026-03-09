using System.Text.Json;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Optimization.Space;

namespace AlgoTradeForge.Application.Optimization;

// Scales QuoteAsset-annotated strategy parameters from human-readable values to
// tick-denominated longs. Handles both top-level params and module sub-params
// (when passed as ModuleSelection values in the dictionary).
public static class ParameterScaler
{
    public static IDictionary<string, object>? ScaleQuoteAssetParams(
        IOptimizationSpaceProvider spaceProvider,
        string strategyName,
        IDictionary<string, object>? parameters,
        ScaleContext scale)
    {
        if (parameters is null or { Count: 0 })
            return parameters;

        var descriptor = spaceProvider.GetDescriptor(strategyName);
        if (descriptor is null)
            return parameters;

        var scaled = new Dictionary<string, object>(parameters);
        ScaleAxes(descriptor.Axes, scaled, scale);
        return scaled;
    }

    private static void ScaleAxes(
        IReadOnlyList<ParameterAxis> axes,
        IDictionary<string, object> parameters,
        ScaleContext scale)
    {
        foreach (var axis in axes)
        {
            switch (axis)
            {
                case NumericRangeAxis { Unit: ParamUnit.QuoteAsset } numeric:
                    if (parameters.TryGetValue(numeric.Name, out var value))
                    {
                        var decimalValue = value is JsonElement je
                            ? je.GetDecimal()
                            : Convert.ToDecimal(value);
                        parameters[numeric.Name] = scale.AmountToTicks(decimalValue);
                    }
                    break;

                case ModuleSlotAxis moduleSlot:
                    if (parameters.TryGetValue(moduleSlot.Name, out var moduleValue)
                        && moduleValue is ModuleSelection selection)
                    {
                        var variant = moduleSlot.Variants
                            .FirstOrDefault(v => v.TypeKey == selection.TypeKey);

                        if (variant is not null && selection.Params.Count > 0)
                        {
                            var scaledSubParams = new Dictionary<string, object>(selection.Params);
                            ScaleAxes(variant.Axes, scaledSubParams, scale);
                            parameters[moduleSlot.Name] = new ModuleSelection(
                                selection.TypeKey, scaledSubParams);
                        }
                    }
                    break;
            }
        }
    }
}
