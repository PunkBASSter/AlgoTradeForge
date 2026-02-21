using AlgoTradeForge.Application.Events;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Infrastructure.Events;

public sealed class JsonlRunSinkFactory(IOptions<EventLogStorageOptions> options) : IRunSinkFactory
{
    public IRunSink Create(RunIdentity identity) => new JsonlFileSink(identity, options.Value);
}
