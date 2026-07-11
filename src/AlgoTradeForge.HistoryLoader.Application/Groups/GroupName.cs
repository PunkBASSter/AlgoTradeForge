using System.Text.RegularExpressions;

namespace AlgoTradeForge.HistoryLoader.Application.Groups;

public static class GroupName
{
    public static readonly Regex Regex =
        new(@"^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.Compiled);

    public static bool IsValid(string? name) => Regex.IsMatch(name ?? "");
}
