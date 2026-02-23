namespace AlgoTradeForge.CandleIngestor.IO;

public sealed class IngestorFileStorage
{
    public string ReadAllText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    public void WriteAllText(string path, string content)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(fs);
        writer.Write(content);
    }

    public async Task WriteAllTextAsync(string path, string content, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(fs);
        await writer.WriteAsync(content.AsMemory(), ct);
    }
}
