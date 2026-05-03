using System.Text.RegularExpressions;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Q-4 — month-key extraction + bare-vs-partNumbered collision detection for the partitioned
/// CSV layout (TRD §3.2). The writer (<see cref="PartitionedSinkWriter"/>) atomic-renames a
/// bare <c>&lt;YYYY-MM&gt;.csv</c> to <c>.p01.csv</c> before opening <c>.p02.csv</c>, so a
/// single successful job NEVER produces both forms for the same month. Any reader that
/// observes both is looking at corruption — operator manipulation, a partial migration, or
/// a writer-invariant break — and silent double-loading would skew downstream aggregation.
/// </summary>
/// <remarks>
/// Accepts both naming shapes:
/// <list type="bullet">
///   <item><c>&lt;YYYY-MM&gt;[.pNN].csv</c> — alt-bar partitions and side-feed sidecars
///         (TRD §3.2 / §3.5).</item>
///   <item><c>&lt;YYYY-MM&gt;_&lt;feedSuffix&gt;[.pNN].csv</c> — time-bar partitions where
///         the FeedId (e.g. <c>1m</c>, <c>5m</c>) is appended after the month (TRD §9.3).</item>
/// </list>
/// Filenames that match neither shape return <see langword="false"/> from
/// <see cref="TryParse"/> — they're skipped from collision analysis rather than throwing,
/// so a future filename addition (e.g. a new sibling artifact in the same directory) doesn't
/// crash existing readers.
/// </remarks>
public static class PartitionFilenameParser
{
    private static readonly Regex AltBarOrSidecarPattern =
        new(@"^(?<month>\d{4}-\d{2})(?:\.p(?<part>\d{2}))?\.csv$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TimeBarPattern =
        new(@"^(?<month>\d{4}-\d{2})_[^.]+(?:\.p(?<part>\d{2}))?\.csv$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Extracts the <c>YYYY-MM</c> month key and optional part-number from a partition file
    /// name (no path, just the bare filename). Returns <see langword="false"/> for names that
    /// don't match either supported shape.
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
    /// Throws <see cref="InvalidDataException"/> if any month appears as both a bare partition
    /// and one or more part-numbered partitions in <paramref name="filePaths"/>. Multiple
    /// part-numbered files for the same month (the normal overflow case) do NOT throw.
    /// Filenames that don't match the partition grammar are ignored — the check only flags
    /// real bare-vs-pNN collisions, never spurious failures from unrelated siblings.
    /// </summary>
    public static void EnsureNoDuplicateMonthPartitions(IEnumerable<string> filePaths)
    {
        // Group by month key; for each month, track whether a bare partition exists AND collect
        // the partNumbered paths. Only the (bare && pNN) co-existence is a violation.
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
