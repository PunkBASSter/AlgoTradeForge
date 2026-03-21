using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.CandleIngestion;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Infrastructure.History;

public sealed class FileSystemAvailableAssetsProvider(
    IOptions<CandleStorageOptions> options) : IAvailableAssetsProvider
{
    private readonly Lazy<IReadOnlyList<AvailableAssetInfo>> _cached = new(() => Scan(options.Value.DataRoot));

    public IReadOnlyList<AvailableAssetInfo> GetAvailableAssets() => _cached.Value;

    private static List<AvailableAssetInfo> Scan(string dataRoot)
    {
        var result = new List<AvailableAssetInfo>();

        if (!Directory.Exists(dataRoot))
            return result;

        foreach (var exchangeDir in Directory.EnumerateDirectories(dataRoot))
        {
            var exchange = Path.GetFileName(exchangeDir);

            foreach (var symbolDir in Directory.EnumerateDirectories(exchangeDir))
            {
                if (!HasCandleData(symbolDir))
                    continue;

                var dirName = Path.GetFileName(symbolDir);
                var isFutures = dirName.EndsWith("_fut", StringComparison.OrdinalIgnoreCase);
                var symbol = isFutures ? dirName[..^4] : dirName;

                result.Add(new AvailableAssetInfo(exchange, symbol, isFutures));
            }
        }

        result.Sort((a, b) =>
        {
            var cmp = string.Compare(a.Exchange, b.Exchange, StringComparison.OrdinalIgnoreCase);
            return cmp != 0 ? cmp : string.Compare(a.Symbol, b.Symbol, StringComparison.OrdinalIgnoreCase);
        });

        return result;
    }

    private static bool HasCandleData(string symbolDir)
    {
        // New format: candles/ subdir with any .csv
        var candlesDir = Path.Combine(symbolDir, "candles");
        if (Directory.Exists(candlesDir) && Directory.EnumerateFiles(candlesDir, "*.csv").Any())
            return true;

        // Old format: any 4-digit year subdir with .csv files
        foreach (var subDir in Directory.EnumerateDirectories(symbolDir))
        {
            var name = Path.GetFileName(subDir);
            if (name.Length == 4 && int.TryParse(name, out _)
                && Directory.EnumerateFiles(subDir, "*.csv").Any())
                return true;
        }

        return false;
    }
}
