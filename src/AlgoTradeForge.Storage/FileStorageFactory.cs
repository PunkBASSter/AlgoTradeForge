using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Storage;

public static class FileStorageFactory
{
    public static IFileStorage Build(IServiceProvider sp)
    {
        var opt = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
        return opt.Backend switch
        {
            StorageBackend.S3 => new S3FileStorage(opt.S3, sp.GetRequiredService<ILogger<S3FileStorage>>()),
            _                 => new LocalFileStorage(opt.Local),
        };
    }

    // The tail index has to know the backend layout — Local uses Seek(-N, End) on the
    // OpenRead stream; S3 issues a Range GET. They can't share a single implementation.
    public static IPartitionTailIndex BuildTailIndex(IServiceProvider sp)
    {
        var storage = sp.GetRequiredService<IFileStorage>();
        return storage switch
        {
            S3FileStorage s3 => new S3TailIndex(s3),
            _                => new LocalTailIndex(storage),
        };
    }
}
