using System.Text.RegularExpressions;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Month-key extraction + bare-vs-partNumbered collision detection. The writer atomic-renames
/// bare <c>&lt;YYYY-MM&gt;.csv</c> to <c>.p01.csv</c> before opening <c>.p02.csv</c>, so a
/// single successful job NEVER produces both forms for the same month — observing both
/// indicates corruption (operator edit, partial migration, or writer bug).
/// Accepts <c>&lt;YYYY-MM&gt;[.pNN].csv</c> (alt-bar/sidecar) and
/// <c>&lt;YYYY-MM&gt;_&lt;suffix&gt;[.pNN].csv</c> (time-bar with FeedId suffix); other names
/// return <see langword="false"/> from <see cref="TryParse"/> rather than throw.
/// </summary>
public static class PartitionFilenameParser
{
    private static readonly Regex AltBarOrSidecarPattern =
        new(@"^(?<month>\d{4}-\d{2})(?:\.p(?<part>\d{2}))?\.csv$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TimeBarPattern =
        new(@"^(?<month>\d{4}-\d{2})_[^.]+(?:\.p(?<part>\d{2}))?\.csv$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Extracts the <c>YYYY-MM</c> month key and optional part-number from a bare filename.
    /// Returns <see langword="false"/> for unrecognized shapes.
    /// </summary>
    public static bool TryParse(string fileName, out string monthKey, out int? partNumber)
    {
        monthKey = string.Empty;
        partNumber = null;

        var m = AltBarOrSidecarPattern.Match(fileName);
        if (!m.Success)
            m = TimeBarPattern.Match(fileName);
        if (!m.Success)
            return false;

        monthKey = m.Groups["month"].Value;
        partNumber = m.Groups["part"].Success
            ? int.Parse(m.Groups["part"].Value, System.Globalization.CultureInfo.InvariantCulture)
            : null;
        return true;
    }

    /// <summary>
    /// Throws <see cref="InvalidDataException"/> if any month has both a bare partition AND
    /// part-numbered partitions. Multiple part-numbered files alone (normal overflow) do NOT
    /// throw. Unrecognized filenames are ignored.
    /// </summary>
    public static void EnsureNoDuplicateMonthPartitions(IEnumerable<string> filePaths)
    {
        var byMonth = new Dictionary<string, (string? Bare, List<string> PartNumbered)>(StringComparer.Ordinal);
        foreach (var path in filePaths)
        {
            var fileName = Path.GetFileName(path);
            if (!TryParse(fileName, out var month, out var part))
                continue;

            if (!byMonth.TryGetValue(month, out var entry))
                entry = (null, new List<string>());

            if (part is null)
                entry = (path, entry.PartNumbered);
            else
                entry.PartNumbered.Add(path);

            byMonth[month] = entry;
        }

        var collisions = byMonth
            .Where(kvp => kvp.Value.Bare is not null && kvp.Value.PartNumbered.Count > 0)
            .ToList();
        if (collisions.Count == 0)
            return;

        var details = string.Join("; ", collisions.Select(kvp =>
            $"month {kvp.Key}: bare='{kvp.Value.Bare}' AND partNumbered=[{string.Join(", ", kvp.Value.PartNumbered)}]"));
        throw new InvalidDataException(
            $"Partition layout violation: bare <YYYY-MM>.csv and <YYYY-MM>.pNN.csv co-exist for the same month. " +
            "This is unreachable from a single successful job (writer atomic-renames bare→p01 before opening p02), " +
            "so this state indicates operator manipulation, partial migration, or a writer bug. " +
            $"Details: {details}");
    }
}
