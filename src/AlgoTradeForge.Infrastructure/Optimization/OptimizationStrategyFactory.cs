using System.Reflection;
using System.Text.Json;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Modules;

namespace AlgoTradeForge.Infrastructure.Optimization;

public sealed class OptimizationStrategyFactory : IStrategyFactory, IOptimizationStrategyFactory
{
    private readonly SpaceDescriptorBuilder _descriptorBuilder;

    private static readonly JsonSerializerOptions CaseInsensitiveJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public OptimizationStrategyFactory(SpaceDescriptorBuilder descriptorBuilder)
    {
        _descriptorBuilder = descriptorBuilder;
    }

    public IInt64BarStrategy Create(string strategyName, IIndicatorFactory indicatorFactory, IDictionary<string, object>? parameters = null)
    {
        var descriptor = _descriptorBuilder.GetDescriptor(strategyName)
            ?? throw new ArgumentException($"Strategy '{strategyName}' not found.");

        var paramsInstance = Activator.CreateInstance(descriptor.ParamsType)!;

        if (parameters is not null)
        {
            foreach (var (key, value) in parameters)
            {
                if (TryHandleModuleProperty(descriptor, paramsInstance, key, value))
                    continue;

                SetProperty(descriptor.ParamsType, paramsInstance, key, value);
            }
        }

        return CreateStrategyInstance(descriptor.StrategyType, paramsInstance, indicatorFactory);
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

        return CreateStrategyInstance(descriptor.StrategyType, paramsInstance, PassthroughIndicatorFactory.Instance);
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

    /// <summary>
    /// Returns true if the property is an interface-typed module slot and was handled.
    /// Detection is by property type (interface), not by [OptimizableModule] — those are
    /// independent concerns (deserialization routing vs. optimization discoverability).
    /// </summary>
    private bool TryHandleModuleProperty(
        OptimizationSpaceDescriptor descriptor,
        object paramsInstance,
        string propertyName,
        object value)
    {
        var prop = descriptor.ParamsType.GetProperty(
            propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop is null || !typeof(IStrategyModule).IsAssignableFrom(prop.PropertyType))
            return false;

        // Already a ModuleSelection (e.g. from optimization path)
        if (value is ModuleSelection selection)
        {
            SetModuleProperty(descriptor, paramsInstance, propertyName, selection);
            return true;
        }

        // Parse JsonElement or string-encoded JSON (dispose document if we created one)
        JsonDocument? ownedDoc = null;
        JsonElement? obj;
        try
        {
            if (value is JsonElement { ValueKind: JsonValueKind.Object } el)
            {
                obj = el;
            }
            else if (value is string json && json.TrimStart().StartsWith('{'))
            {
                ownedDoc = JsonDocument.Parse(json);
                obj = ownedDoc.RootElement;
            }
            else
            {
                obj = null;
            }

            if (obj is not { } element)
                throw new ArgumentException(
                    $"Module slot '{propertyName}' requires a JSON object. " +
                    $"Use {{}} to keep the default or {{\"typeKey\": \"<key>\", \"params\": {{...}}}} to select a variant.");

            // Empty object {} — keep the default module instance
            if (element.EnumerateObject().Any() is false)
                return true;

            if (!element.TryGetProperty("typeKey", out var typeKeyElement)
                || typeKeyElement.GetString() is not { } typeKey)
                throw new ArgumentException(
                    $"Module slot '{propertyName}' requires a 'typeKey' property to select the module implementation. " +
                    $"Use {{}} to keep the default. " +
                    $"Expected format: {{\"typeKey\": \"<key>\", \"params\": {{...}}}}");

            // Need a ModuleSlotAxis to resolve the variant — requires [OptimizableModule] on the property
            var slotAxis = descriptor.Axes.OfType<ModuleSlotAxis>()
                .FirstOrDefault(a => a.Name == propertyName)
                ?? throw new ArgumentException(
                    $"Module slot '{propertyName}' is not configurable. " +
                    $"Remove it from the request to use the default.");

            var subParams = new Dictionary<string, object>();
            if (element.TryGetProperty("params", out var paramsElement)
                && paramsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in paramsElement.EnumerateObject())
                    subParams[p.Name] = p.Value.Clone();
            }

            SetModuleProperty(descriptor, paramsInstance, propertyName,
                new ModuleSelection(typeKey, subParams));
            return true;
        }
        finally
        {
            ownedDoc?.Dispose();
        }
    }

    private static readonly HashSet<string> SkippableProperties = ["DataSubscriptions", "FeedSubscriptions"];

    private static void SetProperty(Type type, object instance, string propertyName, object value)
    {
        if (SkippableProperties.Contains(propertyName))
            return;

        var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
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

        // Handle enum conversions (from string name or integer value)
        if (targetType.IsEnum)
        {
            if (value is string s)
                return Enum.Parse(targetType, s, ignoreCase: true);
            return Enum.ToObject(targetType, Convert.ToInt32(value));
        }

        // String containing JSON for complex types (e.g., from DB round-trip via GetRawText)
        if (value is string jsonString && !targetType.IsPrimitive && !targetType.IsEnum)
            return JsonSerializer.Deserialize(jsonString, targetType, CaseInsensitiveJsonOptions)
                ?? throw new ArgumentException($"Cannot deserialize '{jsonString}' as {targetType.Name}.");

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

        if (targetType.IsEnum)
        {
            return element.ValueKind == JsonValueKind.String
                ? Enum.Parse(targetType, element.GetString()!, ignoreCase: true)
                : Enum.ToObject(targetType, element.GetInt32());
        }

        // String containing embedded JSON (e.g., from double-serialized API input)
        if (element.ValueKind == JsonValueKind.String)
            return JsonSerializer.Deserialize(element.GetString()!, targetType, CaseInsensitiveJsonOptions)
                ?? throw new ArgumentException($"Cannot deserialize JSON string as {targetType.Name}.");

        // Object/array with potentially camelCase keys
        return element.Deserialize(targetType, CaseInsensitiveJsonOptions)
            ?? throw new ArgumentException($"Cannot deserialize JSON element as {targetType.Name}.");
    }

    private static IInt64BarStrategy CreateStrategyInstance(Type strategyType, object paramsInstance, IIndicatorFactory indicatorFactory)
    {
        return (IInt64BarStrategy)Activator.CreateInstance(strategyType, paramsInstance, indicatorFactory)!;
    }
}
