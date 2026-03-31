using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Domain.Optimization.Space;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Optimization;

public sealed class NormalizingEnumerableTests
{
    [Fact]
    public void Enumerate_WithoutDuplicates_YieldsAllCombinations()
    {
        var combos = new[]
        {
            MakeCombination(("A", 1), ("B", 10)),
            MakeCombination(("A", 2), ("B", 20)),
            MakeCombination(("A", 3), ("B", 30)),
        };

        var sut = new NormalizingEnumerable(combos, new PassThroughNormalizer());
        var result = sut.Enumerate().ToList();

        Assert.Equal(3, result.Count);
        Assert.Equal(0, sut.SkippedCount);
    }

    [Fact]
    public void Enumerate_WithDuplicatesAfterNormalization_Deduplicates()
    {
        // Combinations that differ only in "Irrelevant", which the normalizer fixes to 1
        var combos = new[]
        {
            MakeCombination(("Mode", "Both"), ("Irrelevant", 1)),
            MakeCombination(("Mode", "Both"), ("Irrelevant", 2)),
            MakeCombination(("Mode", "Both"), ("Irrelevant", 3)),
            MakeCombination(("Mode", "Follow"), ("Irrelevant", 1)),
            MakeCombination(("Mode", "Follow"), ("Irrelevant", 2)),
        };

        var sut = new NormalizingEnumerable(combos, new FixIrrelevantNormalizer());
        var result = sut.Enumerate().ToList();

        // Both+1, Both+2, Both+3 all normalize to Both+1 → 1 unique
        // Follow+1, Follow+2 pass through unchanged → 2 unique
        Assert.Equal(3, result.Count);
        Assert.Equal(2, sut.SkippedCount);
    }

    [Fact]
    public void Enumerate_EmptySource_YieldsNothing()
    {
        var sut = new NormalizingEnumerable([], new PassThroughNormalizer());
        var result = sut.Enumerate().ToList();

        Assert.Empty(result);
        Assert.Equal(0, sut.SkippedCount);
    }

    [Fact]
    public void Enumerate_AllNormalizeToSameKey_YieldsOne()
    {
        var combos = new[]
        {
            MakeCombination(("X", 1)),
            MakeCombination(("X", 2)),
            MakeCombination(("X", 3)),
        };

        var sut = new NormalizingEnumerable(combos, new CollapseAllNormalizer());
        var result = sut.Enumerate().ToList();

        Assert.Single(result);
        Assert.Equal(2, sut.SkippedCount);
    }

    [Fact]
    public void TryCreateNormalizer_ReturnsNull_WhenNotImplemented()
    {
        var result = NormalizingEnumerable.TryCreateNormalizer(typeof(NonNormalizerParams));
        Assert.Null(result);
    }

    [Fact]
    public void TryCreateNormalizer_ReturnsInstance_WhenImplemented()
    {
        var result = NormalizingEnumerable.TryCreateNormalizer(typeof(NormalizerParams));
        Assert.NotNull(result);
        Assert.IsType<NormalizerParams>(result);
    }

    [Fact]
    public void TryCreateNormalizer_ReturnsNull_WhenNoParameterlessConstructor()
    {
        var result = NormalizingEnumerable.TryCreateNormalizer(typeof(NormalizerWithoutDefaultCtor));
        Assert.Null(result);
    }

    private static ParameterCombination MakeCombination(params (string Key, object Value)[] pairs) =>
        new(pairs.ToDictionary(p => p.Key, p => p.Value));

    /// <summary>Returns combinations unchanged.</summary>
    private sealed class PassThroughNormalizer : IParameterNormalizer
    {
        public ParameterCombination Normalize(ParameterCombination combination) => combination;
    }

    /// <summary>Fixes "Irrelevant" to 1 when Mode != "Follow".</summary>
    private sealed class FixIrrelevantNormalizer : IParameterNormalizer
    {
        public ParameterCombination Normalize(ParameterCombination combination)
        {
            if (combination.Values.TryGetValue("Mode", out var mode) && mode is "Follow")
                return combination;

            var normalized = new Dictionary<string, object>(combination.Values) { ["Irrelevant"] = 1 };
            return new ParameterCombination(normalized);
        }
    }

    /// <summary>Collapses all combinations to a single canonical form.</summary>
    private sealed class CollapseAllNormalizer : IParameterNormalizer
    {
        public ParameterCombination Normalize(ParameterCombination combination) =>
            new(new Dictionary<string, object> { ["X"] = 0 });
    }

    private sealed class NormalizerWithoutDefaultCtor : IParameterNormalizer
    {
        public NormalizerWithoutDefaultCtor(int required) => _ = required;
        public ParameterCombination Normalize(ParameterCombination combination) => combination;
    }

    private sealed class NonNormalizerParams { }

    private sealed class NormalizerParams : IParameterNormalizer
    {
        public ParameterCombination Normalize(ParameterCombination combination) => combination;
    }
}
