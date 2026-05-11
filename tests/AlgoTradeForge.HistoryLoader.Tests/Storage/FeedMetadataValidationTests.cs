using System.Text.Json;
using System.Text.Json.Serialization;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Storage;

/// <summary>
/// P1a-5, P1a-6, P1a-7 — manifest schema extension + required-field validation.
/// </summary>
public sealed class FeedMetadataValidationTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"FeedMetadataValidationTests_{Guid.NewGuid():N}");

    private static readonly JsonSerializerOptions WriterOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string AssetDir(string name)
    {
        var path = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FeedsJsonPath(string assetDir) =>
        Path.Combine(assetDir, "feeds.json");

    // -------------------------------------------------------------------------
    // P1a-7 — read-side rejection of malformed aggregated entry
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_AggregatedFeedMissingImbalanceReconstructionMethod_Throws()
    {
        var assetDir = AssetDir("BTCUSDT_BadAggregated");

        // Hand-craft a malformed manifest: 'fidelity' block is present but the required
        // imbalanceReconstructionMethod key is absent. A future refactor that drops the
        // [JsonIgnore(Never)] on FidelityInfo would silently produce this shape.
        const string malformed = """
        {
          "feeds": {
            "EqV_1m_1000": {
              "kind": "aggregated",
              "type": { "code": "EqV", "name": "EqualVolume" },
              "source": { "feed": "1m" },
              "threshold": { "value": 1000, "unit": "base_asset", "inputMode": "absolute" },
              "build": { "barCount": 100 },
              "fidelity": {
                "estimatedOvershootPct": 2.5,
                "actualOvershootPct": 2.4
              }
            }
          }
        }
        """;
        File.WriteAllText(FeedsJsonPath(assetDir), malformed);

        var manager = new FeedSchemaManager();
        var ex = Assert.Throws<FeedMetadataValidationException>(() => manager.Load(assetDir));

        Assert.Contains("imbalanceReconstructionMethod", ex.Message, StringComparison.Ordinal);
        Assert.Contains("EqV_1m_1000", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AggregatedFeedMissingFidelityBlock_Throws()
    {
        var assetDir = AssetDir("BTCUSDT_NoFidelity");

        const string malformed = """
        {
          "feeds": {
            "EqV_1m_2000": {
              "kind": "aggregated",
              "type": { "code": "EqV" },
              "source": { "feed": "1m" }
            }
          }
        }
        """;
        File.WriteAllText(FeedsJsonPath(assetDir), malformed);

        var manager = new FeedSchemaManager();
        var ex = Assert.Throws<FeedMetadataValidationException>(() => manager.Load(assetDir));
        Assert.Contains("fidelity", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AggregatedFeedWithExplicitNullImbalanceReconstructionMethod_Succeeds()
    {
        var assetDir = AssetDir("BTCUSDT_GoodAggregated");

        // The TRD §4 rule is satisfied by an EXPLICIT null — non-EqIV feeds set
        // imbalanceReconstructionMethod to null and the field MUST be present.
        const string wellFormed = """
        {
          "feeds": {
            "EqV_1m_1000": {
              "kind": "aggregated",
              "type": { "code": "EqV", "name": "EqualVolume" },
              "source": { "feed": "1m" },
              "threshold": { "value": 1000, "unit": "base_asset", "inputMode": "absolute" },
              "fidelity": {
                "estimatedOvershootPct": 2.5,
                "imbalanceReconstructionMethod": null
              }
            }
          }
        }
        """;
        File.WriteAllText(FeedsJsonPath(assetDir), wellFormed);

        var manager = new FeedSchemaManager();
        var metadata = manager.Load(assetDir);

        Assert.NotNull(metadata);
        var def = metadata!.Feeds["EqV_1m_1000"];
        Assert.Equal("aggregated", def.Kind);
        Assert.Equal("EqV", def.Type!.Code);
        Assert.Null(def.Fidelity!.ImbalanceReconstructionMethod);
    }

    [Fact]
    public void Load_EqIWithTickSignedReconstructionMethod_Succeeds()
    {
        var assetDir = AssetDir("BTCUSDT_EqI");

        const string wellFormed = """
        {
          "feeds": {
            "EqIV_ticks_500000": {
              "kind": "aggregated",
              "type": { "code": "EqIV", "name": "EqualImbalance" },
              "source": { "feed": "ticks" },
              "threshold": { "value": 500000, "unit": "quote_asset", "inputMode": "absolute" },
              "fidelity": {
                "imbalanceReconstructionMethod": "tick_signed"
              }
            }
          }
        }
        """;
        File.WriteAllText(FeedsJsonPath(assetDir), wellFormed);

        var manager = new FeedSchemaManager();
        var metadata = manager.Load(assetDir);

        Assert.NotNull(metadata);
        Assert.Equal("tick_signed",
            metadata!.Feeds["EqIV_ticks_500000"].Fidelity!.ImbalanceReconstructionMethod);
    }

    // -------------------------------------------------------------------------
    // P1a-6 — write-side: imbalanceReconstructionMethod always serialized
    //                    (even when null) on aggregated entries
    // -------------------------------------------------------------------------

    [Fact]
    public void Serialize_AggregatedFeedWithNullImbalanceReconstructionMethod_EmitsKeyExplicitly()
    {
        // The [JsonIgnore(Condition = Never)] on FidelityInfo.ImbalanceReconstructionMethod
        // overrides the global DefaultIgnoreCondition.WhenWritingNull. Lock that behavior
        // with a serializer-level test so the rule survives schema refactors.
        var def = new FeedDefinition
        {
            Kind = "aggregated",
            Type = new AggregatedTypeInfo { Code = "EqV", Name = "EqualVolume" },
            Source = new AggregatedSourceInfo { Feed = "1m" },
            Threshold = new ThresholdInfo
            {
                Value = 1000m, Unit = "base_asset", InputMode = "absolute",
            },
            Fidelity = new FidelityInfo
            {
                EstimatedOvershootPct = 2.5,
                ImbalanceReconstructionMethod = null,   // explicitly null
            },
        };

        var json = JsonSerializer.Serialize(def, WriterOptions);

        Assert.Contains("\"imbalanceReconstructionMethod\": null", json, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Backward compatibility — legacy time-bar entries (no Kind) are unaffected
    // -------------------------------------------------------------------------

    [Fact]
    public void Load_LegacyFeedsJson_NoValidationErrors()
    {
        var assetDir = AssetDir("BTCUSDT_Legacy");

        // Pre-Phase-1a manifest: only Interval/Columns. Kind absent. No fidelity.
        const string legacy = """
        {
          "feeds": {
            "funding-rate": {
              "interval": "8h",
              "columns": ["rate", "mark"]
            },
            "candle-ext": {
              "interval": "1m",
              "columns": ["quote_vol", "trade_count", "taker_buy_vol", "taker_buy_quote_vol"]
            }
          },
          "candles": {
            "scaleFactor": 100,
            "intervals": ["1m", "1h", "1d"]
          }
        }
        """;
        File.WriteAllText(FeedsJsonPath(assetDir), legacy);

        var manager = new FeedSchemaManager();
        var metadata = manager.Load(assetDir);

        Assert.NotNull(metadata);
        Assert.Equal(2, metadata!.Feeds.Count);
        Assert.Null(metadata.Feeds["funding-rate"].Kind);
        Assert.Equal("8h", metadata.Feeds["funding-rate"].Interval);
    }
}
