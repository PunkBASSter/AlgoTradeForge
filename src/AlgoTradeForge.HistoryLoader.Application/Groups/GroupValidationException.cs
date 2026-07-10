namespace AlgoTradeForge.HistoryLoader.Application.Groups;

public sealed class GroupValidationException : Exception
{
    public GroupValidationException(IReadOnlyList<string> errors)
        : base($"Group validation failed: {string.Join("; ", errors)}")
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}
