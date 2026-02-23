using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Application.IO;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Infrastructure.Events;

public sealed class JsonlRunSinkFactory(IOptions<EventLogStorageOptions> options, IFileStorage fileStorage) : IRunSinkFactory
{
    public IRunSink Create(RunIdentity identity) => new JsonlFileSink(identity, options.Value, fileStorage);
}
