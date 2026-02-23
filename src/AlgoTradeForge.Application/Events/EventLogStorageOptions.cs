namespace AlgoTradeForge.Application.Events;

public sealed record EventLogStorageOptions
{
    public static string DefaultRoot { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlgoTradeForge",
            "Data",
            "EventLogs");

    public string Root { get; set; } = DefaultRoot;
}
