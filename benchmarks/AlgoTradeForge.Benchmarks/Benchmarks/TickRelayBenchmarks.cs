using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using BenchmarkDotNet.Attributes;

namespace AlgoTradeForge.Benchmarks.Benchmarks;

/// <summary>
/// GC-free ingest→archival throughput for the binary tick relay. Drives a synthetic
/// 1000-instrument firehose through <see cref="TickRelayWriter"/> to a temp-dir
/// <see cref="LocalFileSegmentSink"/>. <c>[MemoryDiagnoser]</c> + <c>[Config(typeof(BriefJsonConfig))]</c>
/// per repo convention; the headline number is Allocated, not just Mean.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(BriefJsonConfig))]
public class TickRelayBenchmarks
{
    private const int Instruments = 1000;
    private const int TicksPerInstrument = 100;

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
    public async Task Relay_1000Instruments_100TicksEach()
    {
        var sink = new LocalFileSegmentSink(_tempDir);
        var options = new TickRelayOptions { MaxSegmentBytes = 8L * 1024 * 1024 };
        await using var writer = new TickRelayWriter(sink, options, TimeProvider.System);

        var ids = new int[Instruments];
        for (int i = 0; i < Instruments; i++)
            ids[i] = writer.RegisterInstrument($"SYM{i:D4}", priceScaleExp: 2, qtyScaleExp: 0);

        long seq = 0;
        for (int t = 0; t < TicksPerInstrument; t++)
            for (int i = 0; i < Instruments; i++)
                await writer.Enqueue(ids[i], new TradeTick(t, 5_000_000 + i, 1, ++seq, AggressorSide.Unknown));
    }
}
