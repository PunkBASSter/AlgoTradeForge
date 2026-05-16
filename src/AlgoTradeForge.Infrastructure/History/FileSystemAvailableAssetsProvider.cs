using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Application.IO;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Infrastructure.History;

public sealed class FileSystemAvailableAssetsProvider(
    IFileStorage storage,
    IOptions<CandleStorageOptions> options) : IAvailableAssetsProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<AvailableAssetInfo>? _cached;

    public async Task<IReadOnlyList<AvailableAssetInfo>> GetAvailableAssets(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;
        await _gate.WaitAsync(ct);
        try
        {
            return _cached ??= await Scan(storage, options.Value.DataRoot, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<List<AvailableAssetInfo>> Scan(IFileStorage storage, string dataRoot, CancellationToken ct)
    {
        var result = new List<AvailableAssetInfo>();

        // List all CSVs under dataRoot recursively, then group by the (exchange, asset) prefix that
        // sits two segments above the file. Treats "candles/<*>.csv" and the legacy "<YYYY>/<*>.csv"
        // layouts as proof an asset exists. Object-store-shaped: no per-directory probing.
        var seen = new HashSet<(string Exchange, string Symbol)>();
        var rootPrefix = string.IsNullOrEmpty(dataRoot)
            ? ""
            : (dataRoot.EndsWith(Path.DirectorySeparatorChar) || dataRoot.EndsWith('/')
                ? dataRoot
                : dataRoot + Path.DirectorySeparatorChar);

        await foreach (var key in storage.ListKeys(rootPrefix, suffix: ".csv", recursive: true, ct))
        {
            ct.ThrowIfCancellationRequested();
            var relative = StripRoot(key, dataRoot);
            var segments = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 4) continue;

            var exchange = segments[0];
            var symbolDir = segments[1];
            var third = segments[2];

            var isNewFormat = string.Equals(third, "candles", StringComparison.Ordinal);
            var isLegacyFormat = third.Length == 4 && int.TryParse(third, out _);
            if (!isNewFormat && !isLegacyFormat) continue;

            if (!seen.Add((exchange, symbolDir))) continue;

            var isFutures = symbolDir.EndsWith("_perp", StringComparison.OrdinalIgnoreCase);
            var symbol = isFutures ? symbolDir[..^5] : symbolDir;
            result.Add(new AvailableAssetInfo(exchange, symbol, isFutures));
        }

        result.Sort((a, b) =>
        {
            var cmp = string.Compare(a.Exchange, b.Exchange, StringComparison.OrdinalIgnoreCase);
            return cmp != 0 ? cmp : string.Compare(a.Symbol, b.Symbol, StringComparison.OrdinalIgnoreCase);
        });

        return result;
    }

    private static string StripRoot(string key, string root)
    {
        if (string.IsNullOrEmpty(root)) return key;
        var normalized = key.Replace('\\', '/');
        var rootNorm = root.Replace('\\', '/').TrimEnd('/');
        return normalized.StartsWith(rootNorm + '/', StringComparison.OrdinalIgnoreCase)
            ? normalized[(rootNorm.Length + 1)..]
            : normalized;
    }
}
