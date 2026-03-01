using System.Text.Json;
using System.Text.Json.Serialization;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Domain.Reporting;

namespace AlgoTradeForge.WebApi.Contracts;

public sealed record RunOptimizationRequest
{
    public required string StrategyName { get; init; }

    [JsonConverter(typeof(OptimizationAxesConverter))]
    public Dictionary<string, OptimizationAxisOverride>? OptimizationAxes { get; init; }

    public List<DataSubscriptionDto>? DataSubscriptions { get; init; }
    public required decimal InitialCash { get; init; }
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset EndTime { get; init; }
    public decimal CommissionPerTrade { get; init; }
    public long SlippageTicks { get; init; }
    public int MaxDegreeOfParallelism { get; init; } = -1;
    public long MaxCombinations { get; init; } = 100_000;
    public string SortBy { get; init; } = MetricNames.Default;
    public int MaxTrialsToKeep { get; init; } = 10_000;
    public double? MinProfitFactor { get; init; } = 1.2;
    public double? MaxDrawdownPct { get; init; } = 40.0;
    public double? MinSharpeRatio { get; init; } = 0.5;
    public double? MinSortinoRatio { get; init; } = 0.5;
    public double? MinAnnualizedReturnPct { get; init; } = 2.0;
}

public sealed class OptimizationAxesConverter : JsonConverter<Dictionary<string, OptimizationAxisOverride>>
{
    public override Dictionary<string, OptimizationAxisOverride>? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected object for optimization axes.");

        var result = new Dictionary<string, OptimizationAxisOverride>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return result;

            var propertyName = reader.GetString()!;
            reader.Read();

            result[propertyName] = ReadAxisOverride(ref reader, options);
        }

        throw new JsonException("Unexpected end of JSON.");
    }

    private static OptimizationAxisOverride ReadAxisOverride(
        ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"Expected object for axis override.");

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        // Detect type by examining properties
        if (root.TryGetProperty("fixed", out var fixedValue))
        {
            return new FixedOverride(ReadJsonValue(fixedValue));
        }

        if (root.TryGetProperty("values", out var valuesArray))
        {
            var values = valuesArray.EnumerateArray().Select(ReadJsonValue).ToList();
            return new DiscreteSetOverride(values);
        }

        if (root.TryGetProperty("variants", out var variants))
        {
            var variantDict = new Dictionary<string, Dictionary<string, OptimizationAxisOverride>?>();
            foreach (var variantProp in variants.EnumerateObject())
            {
                if (variantProp.Value.ValueKind == JsonValueKind.Null)
                {
                    variantDict[variantProp.Name] = null;
                    continue;
                }

                var subOverrides = new Dictionary<string, OptimizationAxisOverride>();
                foreach (var subProp in variantProp.Value.EnumerateObject())
                {
                    subOverrides[subProp.Name] = ReadAxisOverrideFromElement(subProp.Value);
                }

                variantDict[variantProp.Name] = subOverrides;
            }

            return new ModuleChoiceOverride(variantDict);
        }

        if (root.TryGetProperty("min", out var min) &&
            root.TryGetProperty("max", out var max) &&
            root.TryGetProperty("step", out var step))
        {
            return new RangeOverride(min.GetDecimal(), max.GetDecimal(), step.GetDecimal());
        }

        throw new JsonException("Axis override must have 'min/max/step', 'fixed', 'values', or 'variants' properties.");
    }

    private static OptimizationAxisOverride ReadAxisOverrideFromElement(JsonElement element)
    {
        if (element.TryGetProperty("fixed", out var fixedValue))
        {
            return new FixedOverride(ReadJsonValue(fixedValue));
        }

        if (element.TryGetProperty("values", out var valuesArray))
        {
            var values = valuesArray.EnumerateArray().Select(ReadJsonValue).ToList();
            return new DiscreteSetOverride(values);
        }

        if (element.TryGetProperty("variants", out var variants))
        {
            var variantDict = new Dictionary<string, Dictionary<string, OptimizationAxisOverride>?>();
            foreach (var variantProp in variants.EnumerateObject())
            {
                if (variantProp.Value.ValueKind == JsonValueKind.Null)
                {
                    variantDict[variantProp.Name] = null;
                    continue;
                }

                var subOverrides = new Dictionary<string, OptimizationAxisOverride>();
                foreach (var subProp in variantProp.Value.EnumerateObject())
                {
                    subOverrides[subProp.Name] = ReadAxisOverrideFromElement(subProp.Value);
                }

                variantDict[variantProp.Name] = subOverrides;
            }

            return new ModuleChoiceOverride(variantDict);
        }

        if (element.TryGetProperty("min", out var min) &&
            element.TryGetProperty("max", out var max) &&
            element.TryGetProperty("step", out var step))
        {
            return new RangeOverride(min.GetDecimal(), max.GetDecimal(), step.GetDecimal());
        }

        throw new JsonException("Axis override must have 'min/max/step', 'fixed', 'values', or 'variants' properties.");
    }

    private static object ReadJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var l) => l,
            JsonValueKind.Number => element.GetDecimal(),
            JsonValueKind.String => element.GetString()!,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => element.GetRawText()
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        Dictionary<string, OptimizationAxisOverride> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        foreach (var (key, axisOverride) in value)
        {
            writer.WritePropertyName(key);
            WriteAxisOverride(writer, axisOverride);
        }

        writer.WriteEndObject();
    }

    private static void WriteAxisOverride(Utf8JsonWriter writer, OptimizationAxisOverride axisOverride)
    {
        switch (axisOverride)
        {
            case RangeOverride range:
                writer.WriteStartObject();
                writer.WriteNumber("min", range.Min);
                writer.WriteNumber("max", range.Max);
                writer.WriteNumber("step", range.Step);
                writer.WriteEndObject();
                break;

            case FixedOverride fix:
                writer.WriteStartObject();
                writer.WritePropertyName("fixed");
                JsonSerializer.Serialize(writer, fix.Value);
                writer.WriteEndObject();
                break;

            case DiscreteSetOverride discrete:
                writer.WriteStartObject();
                writer.WritePropertyName("values");
                writer.WriteStartArray();
                foreach (var val in discrete.Values)
                    JsonSerializer.Serialize(writer, val);
                writer.WriteEndArray();
                writer.WriteEndObject();
                break;

            case ModuleChoiceOverride module:
                writer.WriteStartObject();
                writer.WritePropertyName("variants");
                writer.WriteStartObject();
                foreach (var (variantKey, subOverrides) in module.Variants)
                {
                    writer.WritePropertyName(variantKey);
                    if (subOverrides is null)
                    {
                        writer.WriteNullValue();
                    }
                    else
                    {
                        writer.WriteStartObject();
                        foreach (var (subKey, subOverride) in subOverrides)
                        {
                            writer.WritePropertyName(subKey);
                            WriteAxisOverride(writer, subOverride);
                        }

                        writer.WriteEndObject();
                    }
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
                break;
        }
    }
}
