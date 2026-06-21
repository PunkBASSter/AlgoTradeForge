using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using BenchmarkDotNet.Attributes;

namespace AlgoTradeForge.Benchmarks.Benchmarks;

/// <summary>
/// GC-free ingest→archival throughput for the binary tick relay. Drives a synthetic
/// 1000-instrument firehose of trades AND quotes through <see cref="RelayWriter"/> to a
/// temp-dir <see cref="LocalSegmentSink"/>. <c>[MemoryDiagnoser]</c> +
/// <c>[Config(typeof(BriefJsonConfig))]</c> per repo convention; the headline number is
/// Allocated/op (per-frame path must be near the pooled-buffer floor).
/// </summary>
[MemoryDiagnoser]
[Config(typeof(BriefJsonConfig))]
public class RelayBenchmarks
{
    private const int Instruments = 1000;
    private const int EventsPerInstrument = 100;

    private string _tempDir = null!;

    [GlobalSetup]
    public void Setup() => _tempDir = Path.Combine(Path.GetTempPath(), $"RelayBench_{Guid.NewGuid():N}");

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Benchmark]
    public async Task Relay_1000Instruments_TradesAndQuotes()
    {
        await using var writer = new RelayWriter("bench", new LocalSegmentSink(_tempDir),
            new StreamPipelineOptions { MaxSegmentBytes = 8L * 1024 * 1024 },
            TimeProvider.System, TimeSpan.FromSeconds(30));
        await writer.Start();

        var ids = new int[Instruments];
        for (int i = 0; i < Instruments; i++)
            ids[i] = writer.RegisterInstrument($"SYM{i:D4}", priceScaleExp: 2, qtyScaleExp: 0);

        long seq = 0;
        for (int t = 0; t < EventsPerInstrument; t++)
            for (int i = 0; i < Instruments; i++)
            {
                await writer.WriteTrade(ids[i], new TradeTick(t, 5_000_000 + i, 1, ++seq, AggressorSide.Buy));
                await writer.WriteQuote(ids[i], new QuoteTick(t, 5_000_000 + i - 1, 5, 5_000_000 + i + 1, 7, ++seq));
            }
    }
}
