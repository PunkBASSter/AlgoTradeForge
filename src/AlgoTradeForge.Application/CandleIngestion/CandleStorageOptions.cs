namespace AlgoTradeForge.Application.CandleIngestion;

public sealed record CandleStorageOptions
{
    public static string DefaultDataRoot { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlgoTradeForge",
            "Candles");

    public string DataRoot { get; init; } = DefaultDataRoot;
}
