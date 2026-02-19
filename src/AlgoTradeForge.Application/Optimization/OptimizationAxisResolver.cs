using AlgoTradeForge.Domain.Optimization.Space;

namespace AlgoTradeForge.Application.Optimization;

public sealed class OptimizationAxisResolver
{
    public IReadOnlyList<ResolvedAxis> Resolve(
        IOptimizationSpaceDescriptor descriptor,
        Dictionary<string, OptimizationAxisOverride>? overrides)
    {
        var resolved = new List<ResolvedAxis>();

        foreach (var axis in descriptor.Axes)
        {
            OptimizationAxisOverride? axisOverride = null;
            overrides?.TryGetValue(axis.Name, out axisOverride);

            resolved.Add(axis switch
            {
                NumericRangeAxis numeric => ResolveNumeric(numeric, axisOverride),
                DiscreteSetAxis discrete => ResolveDiscrete(discrete, axisOverride),
                ModuleSlotAxis module => ResolveModuleSlot(module, axisOverride),
                _ => throw new InvalidOperationException($"Unknown axis type: {axis.GetType().Name}")
            });
        }

        ValidateNoUnknownOverrides(descriptor.Axes, overrides,
            key => $"Unknown parameter name '{key}' for strategy '{descriptor.StrategyName}'.");

        return resolved;
    }

    private static ResolvedAxis ResolveNumeric(NumericRangeAxis axis, OptimizationAxisOverride? axisOverride)
    {
        switch (axisOverride)
        {
            case null:
                // Omitted — not optimized. Return empty axis so the factory applies the param default.
                return new ResolvedNumericAxis(axis.Name, []);

            case FixedOverride fix:
            {
                var fixedValue = ConvertNumeric(fix.Value, axis.ClrType);
                var decimalVal = Convert.ToDecimal(fixedValue);
                ValidateWithinBounds(axis, decimalVal, decimalVal);
                return new ResolvedNumericAxis(axis.Name, [fixedValue]);
            }

            case RangeOverride range:
            {
                ValidateWithinBounds(axis, range.Min, range.Max);
                if (range.Step <= 0)
                    throw new ArgumentException($"Step for '{axis.Name}' must be positive.");

                var values = ExpandRange(range.Min, range.Max, range.Step, axis.ClrType);
                return new ResolvedNumericAxis(axis.Name, values);
            }

            case DiscreteSetOverride discrete:
            {
                var converted = discrete.Values
                    .Select(v => ConvertNumeric(v, axis.ClrType))
                    .ToList();

                foreach (var v in converted)
                {
                    var d = Convert.ToDecimal(v);
                    ValidateWithinBounds(axis, d, d);
                }

                return new ResolvedNumericAxis(axis.Name, converted);
            }

            default:
                throw new ArgumentException(
                    $"Invalid override type '{axisOverride.GetType().Name}' for numeric axis '{axis.Name}'.");
        }
    }

    private static ResolvedAxis ResolveDiscrete(DiscreteSetAxis axis, OptimizationAxisOverride? axisOverride)
    {
        return axisOverride switch
        {
            null => new ResolvedDiscreteAxis(axis.Name, axis.Values),
            DiscreteSetOverride discrete => new ResolvedDiscreteAxis(axis.Name, discrete.Values),
            FixedOverride fix => new ResolvedDiscreteAxis(axis.Name, [fix.Value]),
            _ => throw new ArgumentException(
                $"Invalid override type '{axisOverride.GetType().Name}' for discrete axis '{axis.Name}'.")
        };
    }

    private static ResolvedAxis ResolveModuleSlot(
        ModuleSlotAxis axis, OptimizationAxisOverride? axisOverride)
    {
        if (axisOverride is null)
        {
            // Omitted — no module variants selected, skip
            return new ResolvedModuleSlotAxis(axis.Name, []);
        }

        if (axisOverride is not ModuleChoiceOverride moduleChoice)
            throw new ArgumentException(
                $"Module slot '{axis.Name}' requires a module choice override.");

        var variantLookup = axis.Variants.ToDictionary(v => v.TypeKey);
        var resolvedVariants = new List<ResolvedModuleVariant>();

        foreach (var (variantKey, subOverrides) in moduleChoice.Variants)
        {
            if (!variantLookup.TryGetValue(variantKey, out var variantDesc))
                throw new ArgumentException(
                    $"Unknown module variant '{variantKey}' for slot '{axis.Name}'.");

            var subAxes = ResolveSubAxes(variantDesc.Axes, subOverrides);
            resolvedVariants.Add(new ResolvedModuleVariant(variantKey, subAxes));
        }

        return new ResolvedModuleSlotAxis(axis.Name, resolvedVariants);
    }

    private static IReadOnlyList<ResolvedAxis> ResolveSubAxes(
        IReadOnlyList<ParameterAxis> axes,
        Dictionary<string, OptimizationAxisOverride>? overrides)
    {
        var resolved = new List<ResolvedAxis>();

        foreach (var axis in axes)
        {
            OptimizationAxisOverride? axisOverride = null;
            overrides?.TryGetValue(axis.Name, out axisOverride);

            resolved.Add(axis switch
            {
                NumericRangeAxis numeric => ResolveNumeric(numeric, axisOverride),
                DiscreteSetAxis discrete => ResolveDiscrete(discrete, axisOverride),
                ModuleSlotAxis module => ResolveModuleSlot(module, axisOverride),
                _ => throw new InvalidOperationException($"Unknown axis type in module: {axis.GetType().Name}")
            });
        }

        ValidateNoUnknownOverrides(axes, overrides,
            key => $"Unknown parameter name '{key}' in module variant override.");

        return resolved;
    }

    private static void ValidateNoUnknownOverrides(
        IReadOnlyList<ParameterAxis> axes,
        Dictionary<string, OptimizationAxisOverride>? overrides,
        Func<string, string> messageFactory)
    {
        if (overrides is null) return;

        var knownNames = axes.Select(a => a.Name).ToHashSet();
        foreach (var key in overrides.Keys)
        {
            if (!knownNames.Contains(key))
                throw new ArgumentException(messageFactory(key));
        }
    }

    private static void ValidateWithinBounds(NumericRangeAxis axis, decimal min, decimal max)
    {
        if (min < axis.Min)
            throw new ArgumentException(
                $"Min value {min} for '{axis.Name}' is below the declared minimum {axis.Min}.");
        if (max > axis.Max)
            throw new ArgumentException(
                $"Max value {max} for '{axis.Name}' exceeds the declared maximum {axis.Max}.");
        if (min > max)
            throw new ArgumentException(
                $"Min value {min} for '{axis.Name}' exceeds max value {max}.");
    }

    private static IReadOnlyList<object> ExpandRange(decimal min, decimal max, decimal step, Type clrType)
    {
        var values = new List<object>();
        for (var i = 0; min + i * step <= max; i++)
        {
            values.Add(ConvertNumeric(min + i * step, clrType));
        }

        return values;
    }

    private static object ConvertNumeric(object value, Type targetType)
    {
        var decimalVal = Convert.ToDecimal(value);

        if (targetType == typeof(decimal)) return decimalVal;
        if (targetType == typeof(double)) return (double)decimalVal;
        if (targetType == typeof(int)) return (int)decimalVal;
        if (targetType == typeof(long)) return (long)decimalVal;

        throw new InvalidOperationException($"Unsupported numeric type: {targetType.Name}");
    }
}
