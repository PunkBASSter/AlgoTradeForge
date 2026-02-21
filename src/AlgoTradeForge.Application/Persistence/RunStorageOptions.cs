namespace AlgoTradeForge.Application.Persistence;

public sealed record RunStorageOptions
{
    public static string DefaultDatabasePath { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlgoTradeForge",
            "Data",
            "runs.sqlite");

    public string DatabasePath { get; init; } = DefaultDatabasePath;
}
