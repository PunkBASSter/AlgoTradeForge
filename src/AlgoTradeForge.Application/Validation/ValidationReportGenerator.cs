using System.Text;
using System.Text.Json;
using AlgoTradeForge.Application.Persistence;

namespace AlgoTradeForge.Application.Validation;

/// <summary>
/// Generates a standalone HTML report from a completed validation run.
/// Uses inline CSS for print-friendliness (no external dependencies).
/// </summary>
public static class ValidationReportGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string GenerateHtml(ValidationRunRecord record)
    {
        var sb = new StringBuilder(8192);

        var verdictColor = record.Verdict switch
        {
            "Green" => "#22c55e",
            "Yellow" => "#eab308",
            _ => "#ef4444",
        };

        sb.Append($$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Validation Report — {{record.StrategyName}}</title>
            <style>
              * { box-sizing: border-box; margin: 0; padding: 0; }
              body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', system-ui, sans-serif; color: #e5e5e5; background: #0a0a0a; padding: 2rem; max-width: 900px; margin: 0 auto; }
              h1 { font-size: 1.5rem; margin-bottom: 0.25rem; }
              h2 { font-size: 1.1rem; margin: 1.5rem 0 0.5rem; border-bottom: 1px solid #333; padding-bottom: 0.25rem; }
              .meta { color: #a3a3a3; font-size: 0.85rem; margin-bottom: 1rem; }
              .verdict { display: inline-block; padding: 0.25rem 0.75rem; border-radius: 0.375rem; font-weight: 600; font-size: 0.9rem; color: #fff; }
              .score { font-size: 2rem; font-weight: 700; margin: 0.5rem 0; }
              .summary { color: #d4d4d4; font-size: 0.9rem; margin-bottom: 1rem; }
              table { width: 100%; border-collapse: collapse; margin-bottom: 1rem; font-size: 0.85rem; }
              th, td { text-align: left; padding: 0.5rem 0.75rem; border-bottom: 1px solid #262626; }
              th { color: #a3a3a3; font-weight: 600; text-transform: uppercase; font-size: 0.75rem; letter-spacing: 0.05em; }
              .pass { color: #22c55e; }
              .fail { color: #ef4444; }
              .rejection { background: #1c1c1c; border: 1px solid #ef4444; border-radius: 0.375rem; padding: 0.5rem 0.75rem; margin: 0.25rem 0; font-size: 0.85rem; color: #fca5a5; }
              .category-bar { height: 8px; border-radius: 4px; background: #262626; overflow: hidden; }
              .category-fill { height: 100%; border-radius: 4px; }
              @media print { body { background: #fff; color: #000; } th, td { border-bottom-color: #ccc; } .meta { color: #666; } .rejection { border-color: #ef4444; background: #fef2f2; } }
            </style>
            </head>
            <body>
            """);

        // Header
        sb.Append($"""
            <h1>Validation Report: {Escape(record.StrategyName)}</h1>
            <p class="meta">
              Version {Escape(record.StrategyVersion ?? "—")} &middot;
              Profile: {Escape(record.ThresholdProfileName)} &middot;
              {record.StartedAt:yyyy-MM-dd HH:mm} UTC &middot;
              Duration: {record.DurationMs / 1000.0:F1}s
            </p>
            """);

        // Verdict + Score
        sb.Append($"""
            <div style="margin-bottom:1rem;">
              <span class="verdict" style="background:{verdictColor}">{Escape(record.Verdict)}</span>
            </div>
            <div class="score" style="color:{verdictColor}">{record.CompositeScore:F0} / 100</div>
            """);

        if (record.VerdictSummary is not null)
            sb.Append($"""<p class="summary">{Escape(record.VerdictSummary)}</p>""");

        // Rejections
        var rejections = DeserializeList(record.RejectionsJson);
        if (rejections.Count > 0)
        {
            sb.Append("<h2>Hard Rejections</h2>");
            foreach (var r in rejections)
                sb.Append($"""<div class="rejection">{Escape(r)}</div>""");
        }

        // Category Scores
        var categories = DeserializeDictionary(record.CategoryScoresJson);
        if (categories.Count > 0)
        {
            sb.Append("<h2>Category Scores</h2><table><thead><tr><th>Category</th><th>Score</th><th></th></tr></thead><tbody>");
            foreach (var (name, score) in categories)
            {
                var barColor = score >= 70 ? "#22c55e" : score >= 40 ? "#eab308" : "#ef4444";
                sb.Append($"""
                    <tr>
                      <td>{Escape(name)}</td>
                      <td>{score:F1}</td>
                      <td style="width:60%"><div class="category-bar"><div class="category-fill" style="width:{Math.Min(score, 100):F0}%;background:{barColor}"></div></div></td>
                    </tr>
                    """);
            }
            sb.Append("</tbody></table>");
        }

        // Pipeline Stages
        sb.Append("<h2>Pipeline Stages</h2>");
        sb.Append("<table><thead><tr><th>#</th><th>Stage</th><th>In</th><th>Out</th><th>Duration</th></tr></thead><tbody>");
        foreach (var stage in record.StageResults)
        {
            var passClass = stage.CandidatesOut > 0 ? "pass" : "fail";
            sb.Append($"""
                <tr>
                  <td>{stage.StageNumber}</td>
                  <td>{Escape(stage.StageName)}</td>
                  <td>{stage.CandidatesIn}</td>
                  <td class="{passClass}">{stage.CandidatesOut}</td>
                  <td>{stage.DurationMs}ms</td>
                </tr>
                """);
        }
        sb.Append("</tbody></table>");

        // Summary stats
        sb.Append($"""
            <h2>Summary</h2>
            <table>
              <tr><td>Optimization Run</td><td>{record.OptimizationRunId}</td></tr>
              <tr><td>Candidates In</td><td>{record.CandidatesIn}</td></tr>
              <tr><td>Candidates Out</td><td>{record.CandidatesOut}</td></tr>
              <tr><td>Invocation Count</td><td>{record.InvocationCount}</td></tr>
              <tr><td>Validation ID</td><td>{record.Id}</td></tr>
            </table>
            """);

        sb.Append("""
            <p style="margin-top:2rem;color:#525252;font-size:0.75rem;text-align:center;">
              Generated by AlgoTradeForge Overfitting Detection Pipeline
            </p>
            </body></html>
            """);

        return sb.ToString();
    }

    private static string Escape(string? value) =>
        System.Net.WebUtility.HtmlEncode(value ?? "");

    private static List<string> DeserializeList(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? []; }
        catch { return []; }
    }

    private static Dictionary<string, double> DeserializeDictionary(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, double>>(json, JsonOptions) ?? []; }
        catch { return []; }
    }
}
