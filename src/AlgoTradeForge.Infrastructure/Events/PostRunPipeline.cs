using AlgoTradeForge.Application.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Infrastructure.Events;

public sealed class PostRunPipeline(
    IEventIndexBuilder indexBuilder,
    ITradeDbWriter tradeDbWriter,
    IOptions<PostRunPipelineOptions> options,
    ILogger<PostRunPipeline> logger) : IPostRunPipeline
{
    public PostRunResult Execute(string runFolderPath, RunIdentity identity, RunSummary summary)
    {
        bool indexBuilt = false;
        bool tradesInserted = false;
        string? indexError = null;
        string? tradesError = null;

        // Step 1: Build debug index (if enabled)
        if (options.Value.BuildDebugIndex)
        {
            try
            {
                indexBuilder.Build(runFolderPath);
                indexBuilt = true;
            }
            catch (Exception ex)
            {
                indexError = ex.Message;
                logger.LogError(ex, "Failed to build event index for {RunFolder}", runFolderPath);
            }
        }

        // Step 2: Write trades (always)
        try
        {
            tradeDbWriter.WriteFromJsonl(runFolderPath, identity, summary);
            tradesInserted = true;
        }
        catch (Exception ex)
        {
            tradesError = ex.Message;
            logger.LogError(ex, "Failed to write trades for {RunFolder}", runFolderPath);
        }

        return new PostRunResult(indexBuilt, tradesInserted, indexError, tradesError);
    }
}
