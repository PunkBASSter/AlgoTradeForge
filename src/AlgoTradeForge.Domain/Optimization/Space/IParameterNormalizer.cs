namespace AlgoTradeForge.Domain.Optimization.Space;

/// <summary>
/// Optional interface for strategy parameter classes. Normalizes combinations
/// by fixing irrelevant parameters to canonical values, enabling the optimizer
/// to skip duplicate trials.
/// </summary>
public interface IParameterNormalizer
{
    /// <summary>
    /// Returns a normalized copy of <paramref name="combination"/> where conditionally
    /// irrelevant parameters are set to canonical values. If no normalization is needed,
    /// returns the original instance unchanged.
    /// </summary>
    ParameterCombination Normalize(ParameterCombination combination);
}
