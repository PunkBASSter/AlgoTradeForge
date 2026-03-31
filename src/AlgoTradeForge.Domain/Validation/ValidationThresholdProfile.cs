namespace AlgoTradeForge.Domain.Validation;

/// <summary>
/// Configurable threshold profile for the 8-stage validation pipeline.
/// Each stage has its own threshold record with sensible defaults.
/// </summary>
public sealed record ValidationThresholdProfile
{
    public required string Name { get; init; }
    public Stage0PreFlightThresholds PreFlight { get; init; } = new();
    public Stage1BasicProfitabilityThresholds BasicProfitability { get; init; } = new();
    public Stage2StatisticalSignificanceThresholds StatisticalSignificance { get; init; } = new();
    public Stage3ParameterLandscapeThresholds ParameterLandscape { get; init; } = new();
    public Stage4WalkForwardOptimizationThresholds WalkForwardOptimization { get; init; } = new();
    public Stage5WalkForwardMatrixThresholds WalkForwardMatrix { get; init; } = new();
    public Stage6MonteCarloPnlDeltasPermutationThresholds MonteCarloPermutation { get; init; } = new();
    public Stage7SelectionBiasAuditThresholds SelectionBiasAudit { get; init; } = new();
    public SafetyFloorThresholds SafetyFloors { get; init; } = new();

    public sealed record Stage0PreFlightThresholds
    {
        /// <summary>Minimum bars per trade for data sufficiency.</summary>
        public int MinBarsPerTrade { get; init; } = 1;

        /// <summary>Timestamp gap detection: gaps exceeding this multiple of the median interval are flagged.</summary>
        public double MaxGapRatio { get; init; } = 3.0;

        /// <summary>Maximum number of detected timestamp gaps before failing.</summary>
        public int MaxAllowedGaps { get; init; } = 10;

        /// <summary>Multiplier on the MinBTL formula result (set > 1.0 for extra conservatism).</summary>
        public double MinBtlSafetyFactor { get; init; } = 1.0;

        /// <summary>Reject all candidates if every trial has zero commissions (fantasy backtest).</summary>
        public bool RequireNonZeroCosts { get; init; } = true;
    }

    public sealed record Stage1BasicProfitabilityThresholds
    {
        public decimal MinNetProfit { get; init; } = 0m;
        public double MinProfitFactor { get; init; } = 1.05;
        public int MinTradeCount { get; init; } = 30;
        public double MinTStatistic { get; init; } = 2.0;
        public double MaxDrawdownPct { get; init; } = 40.0;
    }

    public sealed record Stage2StatisticalSignificanceThresholds
    {
        public double DsrPValue { get; init; } = 0.05;
        public double MinPsr { get; init; } = 0.95;
        public double MinProfitFactor { get; init; } = 1.20;
        public double MinRecoveryFactor { get; init; } = 1.5;
        public double MinSharpe { get; init; } = 0.5;
    }

    public sealed record Stage3ParameterLandscapeThresholds
    {
        public double MaxDegradationPct { get; init; } = 0.30;
        public double MinClusterConcentration { get; init; } = 0.50;
        public int SensitivityIterations { get; init; } = 500;
        public double SensitivityRange { get; init; } = 0.10;
    }

    public sealed record Stage4WalkForwardOptimizationThresholds
    {
        public double MinWfe { get; init; } = 0.50;
        public double MinProfitableWindowsPct { get; init; } = 0.70;
        public double MaxOosDrawdownExcess { get; init; } = 0.50;
        public int MinWfoRuns { get; init; } = 5;
        public double OosPct { get; init; } = 0.20;
    }

    public sealed record Stage5WalkForwardMatrixThresholds
    {
        public int[] PeriodCounts { get; init; } = [4, 6, 8, 10, 12, 15];
        public double[] OosPcts { get; init; } = [0.15, 0.20, 0.25];
        public int MinContiguousRows { get; init; } = 3;
        public int MinContiguousCols { get; init; } = 3;
        public int MinCellsPassing { get; init; } = 7;
        public double MinWfe { get; init; } = 0.50;
    }

    public sealed record Stage6MonteCarloPnlDeltasPermutationThresholds
    {
        public int BootstrapIterations { get; init; } = 1000;
        public double MaxDrawdownMultiplier { get; init; } = 1.5;
        public int PermutationIterations { get; init; } = 1000;
        public double MaxPermutationPValue { get; init; } = 0.05;
        public double CostStressMultiplier { get; init; } = 2.0;
    }

    public sealed record Stage7SelectionBiasAuditThresholds
    {
        public int CscvBlocks { get; init; } = 16;
        public double MaxPbo { get; init; } = 0.30;
        public double MinProfitableSubPeriods { get; init; } = 0.70;
        public double MinR2 { get; init; } = 0.85;
        public int SubPeriodCount { get; init; } = 8;
        public int RollingSharpeWindow { get; init; } = 60;
        public double MaxSharpeDecaySlope { get; init; } = -0.001;
        public int RegimeVolWindow { get; init; } = 60;
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
        BasicProfitability = new Stage1BasicProfitabilityThresholds
        {
            MinProfitFactor = 1.10,
            MinTradeCount = 50,
            MaxDrawdownPct = 30.0,
        },
        StatisticalSignificance = new Stage2StatisticalSignificanceThresholds
        {
            DsrPValue = 0.01,
            MinPsr = 0.99,
            MinProfitFactor = 1.40,
            MinRecoveryFactor = 2.0,
            MinSharpe = 0.8,
        },
        ParameterLandscape = new Stage3ParameterLandscapeThresholds
        {
            MaxDegradationPct = 0.25,
            MinClusterConcentration = 0.60,
        },
        WalkForwardOptimization = new Stage4WalkForwardOptimizationThresholds
        {
            MinWfe = 0.60,
            MinProfitableWindowsPct = 0.80,
            MaxOosDrawdownExcess = 0.40,
        },
        WalkForwardMatrix = new Stage5WalkForwardMatrixThresholds
        {
            MinWfe = 0.60,
            MinCellsPassing = 8,
        },
        MonteCarloPermutation = new Stage6MonteCarloPnlDeltasPermutationThresholds
        {
            BootstrapIterations = 2000,
            MaxDrawdownMultiplier = 1.3,
            PermutationIterations = 2000,
            MaxPermutationPValue = 0.01,
            CostStressMultiplier = 2.5,
        },
        SelectionBiasAudit = new Stage7SelectionBiasAuditThresholds
        {
            MaxPbo = 0.20,
            MinProfitableSubPeriods = 0.80,
            MinR2 = 0.90,
        },
    };

    public static ValidationThresholdProfile CryptoStandard() => new()
    {
        Name = "Crypto-Standard",
    };

    /// <summary>Names of all built-in threshold profiles.</summary>
    public static IReadOnlyList<string> BuiltInNames { get; } = ["Crypto-Standard", "Crypto-Conservative"];

    public static ValidationThresholdProfile GetByName(string name) => name switch
    {
        "Crypto-Conservative" => CryptoConservative(),
        "Crypto-Standard" => CryptoStandard(),
        _ => throw new ArgumentException($"Unknown threshold profile: '{name}'. Built-in profiles: {string.Join(", ", BuiltInNames)}."),
    };
}
