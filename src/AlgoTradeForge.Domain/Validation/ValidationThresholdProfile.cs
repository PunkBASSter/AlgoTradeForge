namespace AlgoTradeForge.Domain.Validation;

/// <summary>
/// Configurable threshold profile for the 8-stage validation pipeline.
/// Each stage has its own threshold record with sensible defaults.
/// </summary>
public sealed record ValidationThresholdProfile
{
    public required string Name { get; init; }
    public Stage0Thresholds PreFlight { get; init; } = new();
    public Stage1Thresholds BasicProfitability { get; init; } = new();
    public Stage2Thresholds StatisticalSignificance { get; init; } = new();
    public Stage3Thresholds ParameterLandscape { get; init; } = new();
    public Stage4Thresholds WalkForwardOptimization { get; init; } = new();
    public Stage5Thresholds WalkForwardMatrix { get; init; } = new();
    public Stage6Thresholds MonteCarloPermutation { get; init; } = new();
    public Stage7Thresholds SelectionBiasAudit { get; init; } = new();
    public SafetyFloorThresholds SafetyFloors { get; init; } = new();

    public sealed record Stage0Thresholds
    {
        public int MinBarsPerTrade { get; init; } = 1;
    }

    public sealed record Stage1Thresholds
    {
        public decimal MinNetProfit { get; init; } = 0m;
        public double MinProfitFactor { get; init; } = 1.05;
        public int MinTradeCount { get; init; } = 30;
        public double MaxDrawdownPct { get; init; } = 40.0;
    }

    public sealed record Stage2Thresholds
    {
        public double DsrPValue { get; init; } = 0.05;
        public double MinPsr { get; init; } = 0.95;
        public double MinProfitFactor { get; init; } = 1.20;
        public double MinRecoveryFactor { get; init; } = 1.5;
        public double MinSharpe { get; init; } = 0.5;
    }

    public sealed record Stage3Thresholds
    {
        public double MinNeighborCorrelation { get; init; } = 0.3;
    }

    public sealed record Stage4Thresholds
    {
        public double MinWfe { get; init; } = 0.30;
        public int MinWindows { get; init; } = 5;
    }

    public sealed record Stage5Thresholds
    {
        public double MinWfe { get; init; } = 0.30;
    }

    public sealed record Stage6Thresholds
    {
        public double MaxPbo { get; init; } = 0.60;
        public int Permutations { get; init; } = 1000;
    }

    public sealed record Stage7Thresholds
    {
        public double MaxPbo { get; init; } = 0.60;
    }

    public sealed record SafetyFloorThresholds
    {
        public int MinTradeCount { get; init; } = 30;
        public double MaxPbo { get; init; } = 0.60;
        public double MinWfe { get; init; } = 0.30;
    }

    public static ValidationThresholdProfile CryptoConservative() => new()
    {
        Name = "Crypto-Conservative",
        BasicProfitability = new Stage1Thresholds
        {
            MinProfitFactor = 1.10,
            MinTradeCount = 50,
            MaxDrawdownPct = 30.0,
        },
        StatisticalSignificance = new Stage2Thresholds
        {
            DsrPValue = 0.01,
            MinPsr = 0.99,
            MinProfitFactor = 1.40,
            MinRecoveryFactor = 2.0,
            MinSharpe = 0.8,
        },
    };

    public static ValidationThresholdProfile CryptoStandard() => new()
    {
        Name = "Crypto-Standard",
    };

    public static ValidationThresholdProfile GetByName(string name) => name switch
    {
        "Crypto-Conservative" => CryptoConservative(),
        "Crypto-Standard" => CryptoStandard(),
        _ => throw new ArgumentException($"Unknown threshold profile: '{name}'."),
    };
}
