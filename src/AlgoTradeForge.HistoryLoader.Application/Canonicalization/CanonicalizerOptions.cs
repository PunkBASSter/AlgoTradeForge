namespace AlgoTradeForge.HistoryLoader.Application.Canonicalization;

public sealed class CanonicalizerOptions
{
    public const string SectionName = "Canonicalizer";

    public bool Enabled { get; set; }
    public string LiveMdPrefix { get; set; } = "live-md";
    public string CursorPrefix { get; set; } = "_canon-cursors";
    public string Venue { get; set; } = "";
    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>Absolute base dir the canonical CSV writers partition under (the writers'
    /// ResumeFrom does Directory.GetFiles, so this must be a real FS path). Defaults to the
    /// storage DataRoot, set during host wiring.</summary>
    public string AssetDirBase { get; set; } = "";

    /// <summary>instrument -> asset dir relative to AssetDirBase (e.g. "BTCUSDT" -> "binance/BTCUSDT_perp").</summary>
    public Dictionary<string, string> InstrumentAssetDirs { get; set; } = new();
}
