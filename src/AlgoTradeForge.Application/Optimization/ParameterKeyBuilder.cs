using System.Globalization;
using System.Text;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Application.Optimization;

/// <summary>
/// Shared helper for serializing parameter values into deterministic string keys.
/// Used by both <see cref="BoundedTrialQueue"/> (trial dedup) and
/// <see cref="RunGeneticOptimizationCommandHandler"/> (fitness cache).
/// </summary>
internal static class ParameterKeyBuilder
{
    public static void AppendValue(StringBuilder sb, object value)
    {
        switch (value)
        {
            case double d:
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                break;
            case float f:
                sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                break;
            case decimal dec:
                sb.Append(dec.ToString(CultureInfo.InvariantCulture));
                break;
            case int i:
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
                break;
            case long l:
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
                break;
            case DataSubscription sub:
                sb.Append(sub.Asset.Name).Append(':').Append(sub.Asset.Exchange).Append(':').Append(sub.TimeFrame);
                break;
            case bool b:
                sb.Append(b ? '1' : '0');
                break;
            case DataSubscriptionDto dto:
                sb.Append(dto.AssetName).Append(':').Append(dto.Exchange).Append(':').Append(dto.TimeFrame);
                break;
            case ModuleSelection mod:
                sb.Append(mod.TypeKey).Append('{');
                var modFirst = true;
                foreach (var k in mod.Params.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (!modFirst) sb.Append(',');
                    modFirst = false;
                    sb.Append(k).Append('=');
                    AppendValue(sb, mod.Params[k]);
                }
                sb.Append('}');
                break;
            default:
                sb.Append(value.ToString());
                break;
        }
    }
}
