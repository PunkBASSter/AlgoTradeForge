namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public sealed record AutoApplySpec
{
    private static readonly HashSet<string> ValidTypes =
        ["FundingRate", "Dividend", "SwapRate"];

    public string Type { get; }
    public string RateColumn { get; }
    public string? SignConvention { get; }

    public AutoApplySpec(string type, string rateColumn, string? signConvention = null)
    {
        if (!ValidTypes.Contains(type))
            throw new ArgumentException(
                $"Unknown auto-apply type: '{type}'. Valid: {string.Join(", ", ValidTypes)}",
                nameof(type));

        Type = type;
        RateColumn = rateColumn;
        SignConvention = signConvention;
    }
}
