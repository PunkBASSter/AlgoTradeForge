namespace AlgoTradeForge.Domain.Optimization.Genetic;

/// <summary>
/// GA configuration. Zero values trigger auto-sizing via <see cref="GeneticConfigResolver"/>.
/// </summary>
public sealed record GeneticConfig
{
    /// <summary>Population size per generation. 0 = auto-size from axes.</summary>
    public int PopulationSize { get; init; }

    /// <summary>Maximum generations. 0 = auto (MaxEvaluations / PopulationSize).</summary>
    public int MaxGenerations { get; init; }

    /// <summary>Maximum total evaluations. 0 = auto-size from axes.</summary>
    public long MaxEvaluations { get; init; }

    /// <summary>Number of top individuals carried unchanged to next generation.</summary>
    public int EliteCount { get; init; } = 2;

    /// <summary>Probability of crossover vs cloning. Default 0.85.</summary>
    public double CrossoverRate { get; init; } = 0.85;

    /// <summary>Base mutation rate per gene. -1 = auto (1/N).</summary>
    public double MutationRate { get; init; } = -1;

    /// <summary>Tournament selection pool size.</summary>
    public int TournamentSize { get; init; } = 3;

    /// <summary>Generations without improvement before early termination.</summary>
    public int StagnationLimit { get; init; } = 20;

    /// <summary>Optional wall-clock time budget.</summary>
    public TimeSpan? TimeBudget { get; init; }

    /// <summary>SBX distribution index. Higher = children closer to parents.</summary>
    public double SbxEta { get; init; } = 2.0;

    /// <summary>Polynomial mutation distribution index.</summary>
    public double PolynomialMutationEta { get; init; } = 20.0;

    /// <summary>Probability of switching module variant during mutation.</summary>
    public double ModuleVariantMutationRate { get; init; } = 0.1;

    /// <summary>Minimum trades for fitness penalty threshold.</summary>
    public int MinTrades { get; init; } = 10;

    /// <summary>Max drawdown threshold for quadratic penalty.</summary>
    public double MaxDrawdownThreshold { get; init; } = 30.0;

    /// <summary>Custom fitness weights. Null = default composite.</summary>
    public FitnessWeights? Weights { get; init; }

    /// <summary>
    /// When true, caches fitness results by parameter key so duplicate chromosomes
    /// skip evaluation. Saves time when the GA re-discovers identical parameter sets,
    /// but assumes deterministic backtests (same params always produce the same result).
    /// Default: false.
    /// </summary>
    public bool EnableFitnessCache { get; init; }
}

/// <summary>
/// Weights for the composite fitness function.
/// </summary>
public sealed record FitnessWeights
{
    public double SharpeWeight { get; init; } = 0.5;
    public double SortinoWeight { get; init; } = 0.2;
    public double ProfitFactorWeight { get; init; } = 0.15;
    public double AnnualizedReturnWeight { get; init; } = 0.15;
}
