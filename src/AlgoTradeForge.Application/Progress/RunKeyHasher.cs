using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AlgoTradeForge.Application.Progress;

internal static class RunKeyHasher
{
    internal static void AppendSortedParams(StringBuilder sb, IDictionary<string, object> parameters)
    {
        var first = true;
        foreach (var kvp in parameters.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!first) sb.Append(',');
            sb.Append(kvp.Key).Append('=').Append(string.Format(CultureInfo.InvariantCulture, "{0}", kvp.Value));
            first = false;
        }
    }

    internal static string HashString(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
