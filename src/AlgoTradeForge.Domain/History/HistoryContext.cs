namespace AlgoTradeForge.Domain.History;

public static class HistoryContext
{
    private static readonly AsyncLocal<string?> _basePath = new();

    public static string? BasePath
    {
        get => _basePath.Value;
        set => _basePath.Value = value;
    }

    public static IDisposable SetPath(string path)
    {
        var previous = _basePath.Value;
        _basePath.Value = path;
        return new PathScope(previous);
    }

    private sealed class PathScope(string? previous) : IDisposable
    {
        public void Dispose() => _basePath.Value = previous;
    }
}
