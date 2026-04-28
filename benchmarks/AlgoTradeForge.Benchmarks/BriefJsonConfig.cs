using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;

namespace AlgoTradeForge.Benchmarks;

/// <summary>
/// Adds <see cref="JsonExporter.Brief"/> to the default config so each run emits
/// machine-readable <c>*-report-brief.json</c> alongside the markdown table.
/// scripts/perf/save-baseline.ps1 + compare-baseline.ps1 consume it for
/// longitudinal Mean / Allocated tracking across commits.
/// </summary>
public sealed class BriefJsonConfig : ManualConfig
{
    public BriefJsonConfig()
    {
        AddExporter(JsonExporter.Brief);
    }
}
