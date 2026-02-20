using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlgoTradeForge.Domain.Events;

namespace AlgoTradeForge.Application.Events;

public sealed class EventBus : IEventBus
{
    private readonly ExportMode _runMode;
    private readonly ISink[] _sinks;
    private readonly JsonSerializerOptions _payloadOptions;
    private long _sequence;

    public EventBus(ExportMode runMode, IEnumerable<ISink> sinks)
    {
        _runMode = runMode;
        _sinks = sinks.ToArray();
        _payloadOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
    }

    public void Emit<T>(T evt) where T : IBacktestEvent
    {
        if (!T.DefaultExportMode.HasFlag(_runMode))
            return;

        if (evt is ISubscriptionBoundEvent { IsExportable: false })
            return;

        var seq = ++_sequence;

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);

        writer.WriteStartObject();
        writer.WriteString("ts", evt.Timestamp);
        writer.WriteNumber("sq", seq);
        writer.WriteString("_t", T.TypeId);
        writer.WriteString("src", evt.Source);

        writer.WritePropertyName("d");
        JsonSerializer.Serialize(writer, evt, _payloadOptions);

        writer.WriteEndObject();
        writer.Flush();

        var json = buffer.WrittenMemory;
        foreach (var sink in _sinks)
            sink.Write(json);
    }
}
