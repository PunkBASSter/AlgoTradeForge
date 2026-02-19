using System.Reflection;
using System.Text.Json;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Modules;

namespace AlgoTradeForge.Infrastructure.Optimization;

public sealed class OptimizationStrategyFactory : IStrategyFactory, IOptimizationStrategyFactory
{
    private readonly SpaceDescriptorBuilder _descriptorBuilder;

    public OptimizationStrategyFactory(SpaceDescriptorBuilder descriptorBuilder)
    {
        _descriptorBuilder = descriptorBuilder;
    }

    public IInt64BarStrategy Create(string strategyName, IDictionary<string, object>? parameters = null)
    {
        var descriptor = _descriptorBuilder.GetDescriptor(strategyName)
            ?? throw new ArgumentException($"Strategy '{strategyName}' not found.");

        var paramsInstance = Activator.CreateInstance(descriptor.ParamsType)!;

        if (parameters is not null)
        {
            foreach (var (key, value) in parameters)
            {
                SetProperty(descriptor.ParamsType, paramsInstance, key, value);
            }
        }

        return CreateStrategyInstance(descriptor.StrategyType, paramsInstance);
    }

    public IInt64BarStrategy Create(string strategyName, ParameterCombination combination)
    {
        var descriptor = _descriptorBuilder.GetDescriptor(strategyName)
            ?? throw new ArgumentException($"Strategy '{strategyName}' not found.");

        var paramsInstance = Activator.CreateInstance(descriptor.ParamsType)!;

        foreach (var (key, value) in combination.Values)
        {
            if (value is ModuleSelection moduleSelection)
            {
                SetModuleProperty(descriptor, paramsInstance, key, moduleSelection);
            }
            else
            {
                SetProperty(descriptor.ParamsType, paramsInstance, key, value);
            }
        }

        return CreateStrategyInstance(descriptor.StrategyType, paramsInstance);
    }

    private void SetModuleProperty(
        OptimizationSpaceDescriptor descriptor,
        object paramsInstance,
        string propertyName,
        ModuleSelection selection)
    {
        var prop = descriptor.ParamsType.GetProperty(propertyName)
            ?? throw new InvalidOperationException(
                $"Property '{propertyName}' not found on '{descriptor.ParamsType.Name}'.");

        // Find the module slot axis to look up variant metadata
        var slotAxis = descriptor.Axes.OfType<ModuleSlotAxis>()
            .FirstOrDefault(a => a.Name == propertyName)
            ?? throw new InvalidOperationException(
                $"No module slot axis found for '{propertyName}'.");

        var variant = slotAxis.Variants.FirstOrDefault(v => v.TypeKey == selection.TypeKey)
            ?? throw new InvalidOperationException(
                $"Module variant '{selection.TypeKey}' not found for slot '{propertyName}'.");

        object moduleInstance;
        if (variant.ParamsType != typeof(ModuleParamsBase))
        {
            // Create module params and set properties
            var moduleParams = Activator.CreateInstance(variant.ParamsType)!;
            foreach (var (key, value) in selection.Params)
            {
                SetProperty(variant.ParamsType, moduleParams, key, value);
            }

            moduleInstance = Activator.CreateInstance(variant.ImplType, moduleParams)!;
        }
        else
        {
            moduleInstance = Activator.CreateInstance(variant.ImplType)!;
        }

        prop.SetValue(paramsInstance, moduleInstance);
    }

    private static readonly HashSet<string> SkippableProperties = ["DataSubscriptions"];

    private static void SetProperty(Type type, object instance, string propertyName, object value)
    {
        if (SkippableProperties.Contains(propertyName))
            return;

        var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new ArgumentException(
                $"Unknown property '{propertyName}' on type '{type.Name}'.");

        var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
        var converted = ConvertValue(value, targetType);
        prop.SetValue(instance, converted);
    }

    private static object ConvertValue(object value, Type targetType)
    {
        if (targetType.IsInstanceOfType(value))
            return value;

        if (value is JsonElement jsonElement)
            return ConvertJsonElement(jsonElement, targetType);

        // Handle numeric conversions
        if (targetType == typeof(decimal)) return Convert.ToDecimal(value);
        if (targetType == typeof(double)) return Convert.ToDouble(value);
        if (targetType == typeof(int)) return Convert.ToInt32(value);
        if (targetType == typeof(long)) return Convert.ToInt64(value);
        if (targetType == typeof(float)) return Convert.ToSingle(value);

        return Convert.ChangeType(value, targetType);
    }

    private static object ConvertJsonElement(JsonElement element, Type targetType)
    {
        if (targetType == typeof(decimal)) return element.GetDecimal();
        if (targetType == typeof(double)) return element.GetDouble();
        if (targetType == typeof(int)) return element.GetInt32();
        if (targetType == typeof(long)) return element.GetInt64();
        if (targetType == typeof(float)) return (float)element.GetDouble();
        if (targetType == typeof(string)) return element.GetString()!;
        if (targetType == typeof(bool)) return element.GetBoolean();

        return element.Deserialize(targetType)!;
    }

    private static IInt64BarStrategy CreateStrategyInstance(Type strategyType, object paramsInstance)
    {
        return (IInt64BarStrategy)Activator.CreateInstance(strategyType, paramsInstance)!;
    }
}
