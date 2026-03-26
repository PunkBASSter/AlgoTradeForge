using System.Reflection;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Modules;

namespace AlgoTradeForge.Infrastructure.Optimization;

public sealed class SpaceDescriptorBuilder : IOptimizationSpaceProvider
{
    private static readonly HashSet<Type> NumericTypes =
        [typeof(decimal), typeof(double), typeof(int), typeof(long)];

    private readonly int _maxDepth;
    private Dictionary<string, OptimizationSpaceDescriptor>? _cache;
    private readonly Assembly[] _assemblies;

    public SpaceDescriptorBuilder(Assembly[] assemblies, int maxDepth = 10)
    {
        _assemblies = assemblies;
        _maxDepth = maxDepth;
    }

    public IReadOnlyDictionary<string, OptimizationSpaceDescriptor> Build()
    {
        if (_cache is not null)
            return _cache;

        var result = new Dictionary<string, OptimizationSpaceDescriptor>();
        var allTypes = _assemblies.SelectMany(a => a.GetTypes()).ToArray();

        foreach (var strategyType in allTypes)
        {
            var keyAttr = strategyType.GetCustomAttribute<StrategyKeyAttribute>();
            if (keyAttr is null)
                continue;

            if (strategyType.IsAbstract)
                continue;

            var paramsType = ExtractParamsType(strategyType);
            if (paramsType is null)
                continue;

            var axes = ScanProperties(paramsType, allTypes, [], 0);
            result[keyAttr.Key] = new OptimizationSpaceDescriptor(
                keyAttr.Key, strategyType, paramsType, axes);
        }

        _cache = result;
        return result;
    }

    IOptimizationSpaceDescriptor? IOptimizationSpaceProvider.GetDescriptor(string strategyName)
        => GetDescriptor(strategyName);

    IReadOnlyDictionary<string, IOptimizationSpaceDescriptor> IOptimizationSpaceProvider.GetAll()
    {
        var concrete = Build();
        return concrete.ToDictionary(
            kvp => kvp.Key,
            kvp => (IOptimizationSpaceDescriptor)kvp.Value);
    }

    public OptimizationSpaceDescriptor? GetDescriptor(string strategyName)
    {
        var descriptors = Build();
        return descriptors.GetValueOrDefault(strategyName);
    }

    public IReadOnlyDictionary<string, object> GetParameterDefaults(IOptimizationSpaceDescriptor descriptor)
    {
        var instance = Activator.CreateInstance(descriptor.ParamsType)!;
        var defaults = new Dictionary<string, object>();
        foreach (var prop in descriptor.ParamsType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.Name == nameof(StrategyParamsBase.DataSubscriptions)) continue;
            var value = prop.GetValue(instance);
            if (value is not null)
                defaults[prop.Name] = value;
        }
        return defaults;
    }

    private static Type? ExtractParamsType(Type strategyType)
    {
        var current = strategyType.BaseType;
        while (current is not null)
        {
            if (current.IsGenericType &&
                current.GetGenericTypeDefinition() == typeof(StrategyBase<>))
            {
                return current.GetGenericArguments()[0];
            }

            current = current.BaseType;
        }

        return null;
    }

    private IReadOnlyList<ParameterAxis> ScanProperties(
        Type paramsType,
        Type[] allTypes,
        HashSet<Type> visited,
        int depth)
    {
        if (depth > _maxDepth)
            throw new InvalidOperationException(
                $"Module nesting depth exceeded maximum of {_maxDepth} at type '{paramsType.Name}'.");

        if (!visited.Add(paramsType))
            throw new InvalidOperationException(
                $"Circular module reference detected at type '{paramsType.Name}'.");

        var axes = new List<ParameterAxis>();

        foreach (var prop in paramsType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Skip DataSubscriptions — handled specially by the request
            if (prop.Name == nameof(StrategyParamsBase.DataSubscriptions))
                continue;

            var optimizable = prop.GetCustomAttribute<OptimizableAttribute>();
            var moduleSlot = prop.GetCustomAttribute<OptimizableModuleAttribute>();

            if (optimizable is not null)
            {
                if (prop.PropertyType.IsEnum)
                {
                    axes.Add(new DiscreteSetAxis(
                        prop.Name,
                        ResolveEnumValues(prop.PropertyType, optimizable, paramsType.Name, prop.Name),
                        prop.PropertyType));
                }
                else if (NumericTypes.Contains(prop.PropertyType))
                {
                    axes.Add(new NumericRangeAxis(
                        prop.Name,
                        (decimal)optimizable.Min,
                        (decimal)optimizable.Max,
                        (decimal)optimizable.Step,
                        prop.PropertyType,
                        optimizable.Unit));
                }
                else
                {
                    throw new InvalidOperationException(
                        $"[Optimizable] on '{paramsType.Name}.{prop.Name}' requires a numeric or enum type, " +
                        $"but found '{prop.PropertyType.Name}'.");
                }
            }
            else if (moduleSlot is not null)
            {
                if (!prop.PropertyType.IsInterface)
                    throw new InvalidOperationException(
                        $"[OptimizableModule] on '{paramsType.Name}.{prop.Name}' requires an interface type, " +
                        $"but found '{prop.PropertyType.Name}'.");

                var variants = DiscoverModuleVariants(prop.PropertyType, allTypes, visited, depth);
                axes.Add(new ModuleSlotAxis(prop.Name, prop.PropertyType, variants));
            }
        }

        visited.Remove(paramsType);
        return axes;
    }

    private IReadOnlyList<ModuleVariantDescriptor> DiscoverModuleVariants(
        Type interfaceType,
        Type[] allTypes,
        HashSet<Type> visited,
        int depth)
    {
        var variants = new List<ModuleVariantDescriptor>();

        foreach (var implType in allTypes)
        {
            if (implType.IsAbstract || implType.IsInterface)
                continue;

            if (!interfaceType.IsAssignableFrom(implType))
                continue;

            var keyAttr = implType.GetCustomAttribute<ModuleKeyAttribute>();
            if (keyAttr is null)
                continue;

            var moduleParamsType = FindModuleParamsType(implType);
            var subAxes = moduleParamsType is not null
                ? ScanProperties(moduleParamsType, allTypes, visited, depth + 1)
                : [];

            variants.Add(new ModuleVariantDescriptor(
                keyAttr.Key,
                implType,
                moduleParamsType ?? typeof(ModuleParamsBase),
                subAxes));
        }

        return variants;
    }

    private static IReadOnlyList<object> ResolveEnumValues(
        Type enumType, OptimizableAttribute attr, string paramsTypeName, string propName)
    {
        if (attr.Include is not null && attr.Exclude is not null)
            throw new InvalidOperationException(
                $"[Optimizable] on '{paramsTypeName}.{propName}' cannot specify both Include and Exclude.");

        var allValues = Enum.GetValues(enumType).Cast<object>().ToList();

        if (attr.Include is { Length: > 0 })
        {
            var parsed = attr.Include.Select(s => ParseEnumValue(enumType, s, paramsTypeName, propName)).ToList();
            return parsed;
        }

        if (attr.Exclude is { Length: > 0 })
        {
            var excluded = new HashSet<object>(
                attr.Exclude.Select(s => ParseEnumValue(enumType, s, paramsTypeName, propName)));
            return allValues.Where(v => !excluded.Contains(v)).ToList();
        }

        return allValues;
    }

    private static object ParseEnumValue(Type enumType, string value, string paramsTypeName, string propName)
    {
        if (Enum.TryParse(enumType, value, ignoreCase: true, out var result) && result is not null)
            return result;

        if (int.TryParse(value, out var intVal))
            return Enum.ToObject(enumType, intVal);

        throw new InvalidOperationException(
            $"[Optimizable] on '{paramsTypeName}.{propName}': '{value}' is not a valid member of {enumType.Name}. " +
            $"Valid values: {string.Join(", ", Enum.GetNames(enumType))}.");
    }

    private static Type? FindModuleParamsType(Type implType)
    {
        // Look for a constructor with a parameter deriving from ModuleParamsBase
        foreach (var ctor in implType.GetConstructors())
        {
            foreach (var param in ctor.GetParameters())
            {
                if (typeof(ModuleParamsBase).IsAssignableFrom(param.ParameterType))
                    return param.ParameterType;
            }
        }

        return null;
    }
}
