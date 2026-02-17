using AlgoTradeForge.Domain.Optimization.Space;

namespace AlgoTradeForge.Domain.Optimization;

public interface ICartesianProductGenerator
{
    IEnumerable<ParameterCombination> Enumerate(IReadOnlyList<ResolvedAxis> axes);
    long EstimateCount(IReadOnlyList<ResolvedAxis> axes);
}

public sealed class CartesianProductGenerator : ICartesianProductGenerator
{
    public long EstimateCount(IReadOnlyList<ResolvedAxis> axes)
    {
        if (axes.Count == 0)
            return 1;

        long count = 1;
        foreach (var axis in axes)
        {
            checked
            {
                count *= CountAxis(axis);
            }
        }

        return count;
    }

    public IEnumerable<ParameterCombination> Enumerate(IReadOnlyList<ResolvedAxis> axes)
    {
        if (axes.Count == 0)
        {
            yield return new ParameterCombination(new Dictionary<string, object>());
            yield break;
        }

        foreach (var values in EnumerateRecursive(axes, 0))
        {
            yield return new ParameterCombination(values);
        }
    }

    private static IEnumerable<Dictionary<string, object>> EnumerateRecursive(
        IReadOnlyList<ResolvedAxis> axes, int index)
    {
        if (index >= axes.Count)
        {
            yield return new Dictionary<string, object>();
            yield break;
        }

        var axis = axes[index];

        foreach (var axisValue in ExpandAxisValues(axis))
        {
            foreach (var rest in EnumerateRecursive(axes, index + 1))
            {
                // Merge current axis value(s) into the rest
                foreach (var kv in axisValue)
                    rest[kv.Key] = kv.Value;

                yield return rest;
            }
        }
    }

    private static IEnumerable<Dictionary<string, object>> ExpandAxisValues(ResolvedAxis axis)
    {
        switch (axis)
        {
            case ResolvedNumericAxis numeric:
                foreach (var val in numeric.Values)
                    yield return new Dictionary<string, object> { [numeric.Name] = val };
                break;

            case ResolvedDiscreteAxis discrete:
                foreach (var val in discrete.Values)
                    yield return new Dictionary<string, object> { [discrete.Name] = val };
                break;

            case ResolvedModuleSlotAxis moduleSlot:
                foreach (var variant in moduleSlot.Variants)
                {
                    if (variant.SubAxes.Count == 0)
                    {
                        yield return new Dictionary<string, object>
                        {
                            [moduleSlot.Name] = new ModuleSelection(
                                variant.TypeKey, new Dictionary<string, object>())
                        };
                    }
                    else
                    {
                        foreach (var subCombination in EnumerateRecursive(variant.SubAxes, 0))
                        {
                            yield return new Dictionary<string, object>
                            {
                                [moduleSlot.Name] = new ModuleSelection(
                                    variant.TypeKey,
                                    new Dictionary<string, object>(subCombination))
                            };
                        }
                    }
                }

                break;
        }
    }

    private static long CountAxis(ResolvedAxis axis)
    {
        return axis switch
        {
            ResolvedNumericAxis n => n.Values.Count,
            ResolvedDiscreteAxis d => d.Values.Count,
            ResolvedModuleSlotAxis m => m.Variants.Sum(v =>
            {
                if (v.SubAxes.Count == 0) return 1L;
                long subCount = 1;
                foreach (var sub in v.SubAxes)
                    checked { subCount *= CountAxis(sub); }
                return subCount;
            }),
            _ => throw new InvalidOperationException($"Unknown axis type: {axis.GetType().Name}")
        };
    }
}
