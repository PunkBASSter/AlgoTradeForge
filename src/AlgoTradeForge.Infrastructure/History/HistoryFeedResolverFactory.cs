using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Strategy;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Infrastructure.History;

/// <summary>
/// Chooses the <see cref="IHistoryFeedResolver"/> for an asset — the single composition
/// site that maps the data-source axis to a resampling policy. This is the one allowed
/// switch: it returns a strategy, it holds no resolution logic itself. Adding a new
/// policy = a new <see cref="IHistoryFeedResolver"/> + one arm here.
/// </summary>
public sealed class HistoryFeedResolverFactory(
    IFeedManifestReader manifestReader,
    IOptions<CandleStorageOptions> storageOptions)
{
    public IHistoryFeedResolver For(Asset asset)
    {
        var opts = storageOptions.Value;
        return asset switch
        {
            CryptoAsset or CryptoPerpetualAsset =>
                new ResampleFromSourceResolver(new TimeFrame(opts.SourceInterval)),
            _ => new NativeElseDivisorResolver(manifestReader, opts.DataRoot),
        };
    }
}
