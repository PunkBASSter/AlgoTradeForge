using System.Text.Json;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Index;

namespace AlgoTradeForge.HistoryLoader.WebApi.Aggregation;

// AggregationJob is self-contained ("the pipeline never re-reads config"), but ScaleContext is
// not System.Text.Json round-trippable (get-only props, no matching ctor, internal ScaleFactor),
// so the payload persists the asset's DecimalDigits and rebuilds SourceScale == AccumulatorScale
// via AssetScaleContextFactory — the SAME derivation the POST path used at create time.
internal sealed class AggregationRequestRehydrator
{
    private sealed record Payload(
        string DataRoot,
        string Exchange,
        string Asset,
        string SourceFeedId,
        DataFeedKind SourceKind,
        string AssetDir,
        string OutcomeFeedId,
        string TypeCode,
        decimal ThresholdAbsolute,
        long ThresholdScaled,
        string ThresholdUnit,
        string ThresholdInputMode,
        string? ThresholdConvenienceInput,
        int DecimalDigits,
        int MaxPartitionSizeMB,
        string ToolVersion,
        ResumeContext? Resume);

    public static string Serialize(AggregationJob job, int decimalDigits) =>
        JsonSerializer.Serialize(new Payload(
            job.Source.DataRoot,
            job.Source.Exchange,
            job.Source.Asset,
            job.Source.FeedId,
            job.Source.Kind,
            job.AssetDir,
            job.OutcomeFeedId,
            job.TypeCode,
            job.ThresholdAbsolute,
            job.ThresholdScaled,
            job.ThresholdUnit,
            job.ThresholdInputMode,
            job.ThresholdConvenienceInput,
            decimalDigits,
            job.MaxPartitionSizeMB,
            job.ToolVersion,
            job.Resume));

    public AggregationJob Rehydrate(IndexJobRow row)
    {
        if (string.IsNullOrEmpty(row.RequestJson))
            throw new InvalidOperationException($"Aggregation job '{row.Id}' has no request_json to rehydrate.");

        var p = JsonSerializer.Deserialize<Payload>(row.RequestJson)
            ?? throw new InvalidOperationException($"Aggregation job '{row.Id}' request_json deserialized to null.");

        var scale = AssetScaleContextFactory.FromDecimalDigits(p.DecimalDigits);

        return new AggregationJob(
            JobId: row.Id,
            Source: new DataFeedDescriptor(p.DataRoot, p.Exchange, p.Asset, p.SourceFeedId, p.SourceKind),
            AssetDir: p.AssetDir,
            OutcomeFeedId: p.OutcomeFeedId,
            TypeCode: p.TypeCode,
            ThresholdAbsolute: p.ThresholdAbsolute,
            ThresholdScaled: p.ThresholdScaled,
            ThresholdUnit: p.ThresholdUnit,
            ThresholdInputMode: p.ThresholdInputMode,
            ThresholdConvenienceInput: p.ThresholdConvenienceInput,
            SourceScale: scale,
            AccumulatorScale: scale,
            MaxPartitionSizeMB: p.MaxPartitionSizeMB,
            ToolVersion: p.ToolVersion,
            Resume: p.Resume);
    }
}
