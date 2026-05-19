using System.Text;

namespace AlgoTradeForge.Infrastructure.IO;

/// <summary>
/// Shared last-line extraction for the tail-index implementations. Walks a tail-window byte
/// buffer (typically 8 KiB read from the end of a partition) backwards past trailing CR/LF
/// runs, then back to the previous line break. Returns <c>null</c> if the buffer is empty
/// or only contains line terminators.
/// </summary>
internal static class TailExtractor
{
    public static string? ExtractLastLine(byte[] buf, int length)
    {
        if (buf is null) return null;
        if (length <= 0 || length > buf.Length) return null;

        var end = length;
        while (end > 0 && (buf[end - 1] == (byte)'\n' || buf[end - 1] == (byte)'\r')) end--;
        if (end == 0) return null;

        var start = end - 1;
        while (start > 0 && buf[start - 1] != (byte)'\n' && buf[start - 1] != (byte)'\r') start--;

        return Encoding.UTF8.GetString(buf, start, end - start);
    }
}
