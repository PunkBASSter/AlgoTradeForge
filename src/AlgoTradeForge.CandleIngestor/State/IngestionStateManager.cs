using System.Text.Json;
using AlgoTradeForge.CandleIngestor.Storage;

namespace AlgoTradeForge.CandleIngestor.State;

public sealed class IngestionStateManager(string dataRoot, ILogger<IngestionStateManager> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private string GetStatePath(string exchange, string symbol) =>
        Path.Combine(dataRoot, exchange, symbol, "ingestion-state.json");

    public IngestionState? Load(string exchange, string symbol)
    {
        var path = GetStatePath(exchange, symbol);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<IngestionState>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CorruptStateFile: {Path}, will re-bootstrap", path);
            return null;
        }
    }

    public void Save(string exchange, string symbol, IngestionState state)
    {
        var path = GetStatePath(exchange, symbol);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var tmpPath = path + ".tmp";
        var json = JsonSerializer.Serialize(state, JsonOptions);
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, path, overwrite: true);

        logger.LogDebug("StateSaved: {Path}", path);
    }

    public IngestionState Bootstrap(string exchange, string symbol, CsvCandleWriter writer)
    {
        var first = writer.GetFirstTimestamp(exchange, symbol);
        var last = writer.GetLastTimestamp(exchange, symbol);

        var state = new IngestionState
        {
            FirstTimestamp = first,
            LastTimestamp = last,
            LastRunUtc = DateTimeOffset.UtcNow,
            Gaps = []
        };

        logger.LogInformation("StateBootstrapped: {Exchange}/{Symbol}, first={First}, last={Last}",
            exchange, symbol, first, last);

        return state;
    }
}
