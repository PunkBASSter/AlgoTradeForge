using System.Text.Json;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Index;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

public sealed class RecordedScaleTests
{
    [Fact]
    public void TryGetDecimalDigits_ReadsCandleScaleFactor()
    {
        var meta = new FeedMetadata { Candles = new CandleConfig { ScaleFactor = 100m, Intervals = ["1h"] } };
        var manifest = JsonSerializer.Serialize(meta, ManifestJson.Options);

        Assert.True(RecordedScale.TryGetDecimalDigits(manifest, out var digits));
        Assert.Equal(2, digits);
    }

    [Fact]
    public void TryGetDecimalDigits_NoCandleConfig_ReturnsFalse()
    {
        Assert.False(RecordedScale.TryGetDecimalDigits("{}", out _));
    }
}
