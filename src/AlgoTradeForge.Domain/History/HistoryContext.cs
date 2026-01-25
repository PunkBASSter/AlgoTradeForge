namespace AlgoTradeForge.Domain.History;

/// <summary>
/// Provides ambient context for history data paths via AsyncLocal.
/// Use <see cref="SetPath"/> to configure the base path for CSV history files.
/// </summary>
public static class HistoryContext
{
    private static readonly AsyncLocal<string?> _basePath = new();

    /// <summary>
    /// Gets or sets the base path for history CSV files.
    /// This value flows across async continuations automatically.
    /// </summary>
    public static string? BasePath
    {
        get => _basePath.Value;
        set => _basePath.Value = value;
    }

    /// <summary>
    /// Sets the base path and returns a disposable scope that restores the previous value.
    /// </summary>
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
