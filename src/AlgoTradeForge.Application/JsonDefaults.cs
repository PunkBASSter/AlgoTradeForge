using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlgoTradeForge.Application;

/// <summary>
/// Canonical JSON policy. Web defaults plus AllowNamedFloatingPointLiterals (Sharpe etc. can
/// emit NaN/Infinity) and string-enum serialization (robust to ordinal shifts; ordinals still
/// accepted on read).
/// </summary>
public static class JsonDefaults
{
    public static JsonSerializerOptions Api { get; } = CreateApiOptions();

    /// <summary>
    /// Applies the policy to a framework-owned options instance. Adds
    /// <see cref="JsonStringEnumConverter"/> unconditionally — call at most once per instance.
    /// </summary>
    public static void Apply(JsonSerializerOptions options)
    {
        options.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
        options.Converters.Add(new JsonStringEnumConverter());
    }

    private static JsonSerializerOptions CreateApiOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Apply(options);
        return options;
    }
}
