using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;

namespace AlgoTradeForge.Application.Events;

public sealed class EventBus : IEventBus
{
    private readonly ExportMode _runMode;
    private readonly ISink[] _sinks;
    private readonly IDebugProbe? _probe;
    private readonly JsonSerializerOptions _payloadOptions;
    private readonly ArrayBufferWriter<byte> _buffer = new();
    private long _sequence;
    private bool _mutationsEnabled;

    public EventBus(ExportMode runMode, IEnumerable<ISink> sinks, IDebugProbe? probe = null)
    {
        _runMode = runMode;
        _sinks = sinks.ToArray();
        _probe = probe;
        _payloadOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers = { ExcludeEnvelopeProperties },
            },
        };
    }

    public bool MutationsEnabled => Volatile.Read(ref _mutationsEnabled);

    public void SetMutationsEnabled(bool enabled) => Volatile.Write(ref _mutationsEnabled, enabled);

    public void Emit<T>(T evt) where T : IBacktestEvent
    {
        if (!T.DefaultExportMode.HasFlag(_runMode))
            return;

        if (evt is ISubscriptionBoundEvent { IsExportable: false })
            return;

        // Convention: event types ending in ".mut" (e.g. "bar.mut", "ind.mut") are
        // mutation events, gated by the runtime mutations toggle.
        if (!Volatile.Read(ref _mutationsEnabled)
            && T.TypeId.EndsWith(".mut", StringComparison.Ordinal))
            return;

        var seq = ++_sequence;

        _buffer.ResetWrittenCount();
        using var writer = new Utf8JsonWriter(_buffer);

        writer.WriteStartObject();
        writer.WriteString("ts", evt.Timestamp);
        writer.WriteNumber("sq", seq);
        writer.WriteString("_t", T.TypeId);
        writer.WriteString("src", evt.Source);

        writer.WritePropertyName("d");
        JsonSerializer.Serialize(writer, evt, _payloadOptions);

        writer.WriteEndObject();
        writer.Flush();

        var json = _buffer.WrittenMemory;
        foreach (var sink in _sinks)
            sink.Write(json);

        _probe?.OnEventEmitted(T.TypeId, seq);
    }

    /// <summary>
    /// Strips envelope properties (Timestamp, Source, IsExportable) from the "d" payload.
    /// These are written as top-level envelope fields by <see cref="Emit{T}"/> and must not
    /// appear duplicated inside the nested payload object.
    /// </summary>
    private static void ExcludeEnvelopeProperties(JsonTypeInfo typeInfo)
    {
        if (!typeof(IBacktestEvent).IsAssignableFrom(typeInfo.Type))
            return;

        for (var i = typeInfo.Properties.Count - 1; i >= 0; i--)
        {
            if (typeInfo.Properties[i].Name is "timestamp" or "source" or "isExportable")
                typeInfo.Properties.RemoveAt(i);
        }
    }
}
