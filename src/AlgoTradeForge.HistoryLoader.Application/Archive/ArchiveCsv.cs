namespace AlgoTradeForge.HistoryLoader.Application.Archive;

public static class ArchiveCsv
{
    public static IEnumerable<string[]> ReadRows(TextReader reader)
    {
        string? line;
        var first = true;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
                continue;
            if (first)
            {
                first = false;
                if (!char.IsAsciiDigit(line[0]))
                    continue;
            }
            yield return line.Split(',');
        }
    }

    // ms epochs stay < 1e14 until year 5138; µs epochs are >= 1e14 for any date after 1973.
    public static long NormalizeTimestampMs(long raw) =>
        raw >= 100_000_000_000_000 ? raw / 1000 : raw;
}
