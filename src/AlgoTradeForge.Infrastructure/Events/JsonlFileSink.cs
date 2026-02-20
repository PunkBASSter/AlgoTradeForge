using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlgoTradeForge.Application.Events;

namespace AlgoTradeForge.Infrastructure.Events;

public sealed class JsonlFileSink : IRunSink
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private static readonly JsonSerializerOptions MetaJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly RunIdentity _identity;
    private readonly FileStream _stream;
    private bool _disposed;

    public string RunFolderPath { get; }

    public JsonlFileSink(RunIdentity identity, EventLogStorageOptions options)
    {
        _identity = identity;
        RunFolderPath = Path.Combine(options.Root, identity.ComputeFolderName());
        Directory.CreateDirectory(RunFolderPath);

        var eventsPath = Path.Combine(RunFolderPath, "events.jsonl");
        _stream = new FileStream(eventsPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }

    public void Write(ReadOnlyMemory<byte> utf8Json)
    {
        _stream.Write(utf8Json.Span);
        _stream.Write(NewLine);
        _stream.Flush();
    }

    public void WriteMeta(RunSummary summary)
    {
        var meta = new RunMeta
        {
            StrategyName = _identity.StrategyName,
            StrategyVersion = _identity.StrategyVersion,
            AssetName = _identity.AssetName,
            StartTime = _identity.StartTime,
            EndTime = _identity.EndTime,
            InitialCash = _identity.InitialCash,
            RunMode = _identity.RunMode,
            RunTimestamp = _identity.RunTimestamp,
            StrategyParameters = _identity.StrategyParameters,
            TotalBarsProcessed = summary.TotalBarsProcessed,
            FinalEquity = summary.FinalEquity,
            TotalFills = summary.TotalFills,
            Duration = summary.Duration,
        };

        var metaPath = Path.Combine(RunFolderPath, "meta.json");
        var json = JsonSerializer.Serialize(meta, MetaJsonOptions);
        File.WriteAllText(metaPath, json, Encoding.UTF8);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Flush();
        _stream.Dispose();
    }
}
