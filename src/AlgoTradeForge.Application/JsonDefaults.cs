using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlgoTradeForge.Application;

/// <summary>
/// Canonical <see cref="JsonSerializerOptions"/> policy for every JSON surface in the system —
/// FE-facing API responses, persisted blobs in SQLite, and intra-service serialization.
/// One source of truth so wire shape and on-disk shape stay aligned.
/// </summary>
/// <remarks>
/// <para>
/// Policy (applied on top of <see cref="JsonSerializerDefaults.Web"/>, which already supplies
/// camelCase property names + case-insensitive reads):
/// </para>
/// <list type="bullet">
/// <item><c>NumberHandling = AllowNamedFloatingPointLiterals</c> — round-trips NaN / Infinity
/// (metrics like Sharpe ratio can produce these on degenerate inputs).</item>
/// <item><c>JsonStringEnumConverter</c> — enums serialize as strings (e.g.
/// <c>DataFeedRole</c> as <c>"Primary"</c>/<c>"Side"</c>) so the wire shape is robust to
/// ordinal shifts. Integer ordinals are still accepted on read.</item>
/// </list>
/// <para>
/// Three integration patterns:
/// </para>
/// <list type="bullet">
/// <item><see cref="Api"/> — the singleton; inject directly when an immutable
/// pre-configured instance is acceptable.</item>
/// <item><c>new JsonSerializerOptions(JsonDefaults.Api)</c> — copy constructor for
/// consumers that need a mutable instance (e.g. <c>SqliteRunRepository.JsonOptions</c>).</item>
/// <item><see cref="Apply"/> — applies the policy to a framework-owned instance whose
/// allocation we don't control (e.g. <c>ConfigureHttpJsonOptions</c> in
/// <c>Program.cs</c>, which is handed an existing options instance pre-loaded with
/// Web defaults).</item>
/// </list>
/// </remarks>
public static class JsonDefaults
{
    public static JsonSerializerOptions Api { get; } = CreateApiOptions();

    /// <summary>
    /// Applies the canonical policy to an existing <see cref="JsonSerializerOptions"/>
    /// instance. Idempotent for property settings; <see cref="JsonStringEnumConverter"/> is
    /// added unconditionally, so call this exactly once per options instance.
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
