using AlgoTradeForge.Application.Persistence;

namespace AlgoTradeForge.Application.Optimization;

public sealed class FailedTrialCollector(int capacity)
{
    private readonly int _capacity = capacity;
    private readonly Dictionary<(string ExceptionType, string StackTrace), FailureEntry> _entries = new();
    private readonly Lock _lock = new();

    /// <summary>
    /// Records a failed trial. Deduplicates by (exceptionType, stackTrace).
    /// If the entry already exists, increments count. If at capacity, rejects new
    /// signatures but still increments existing ones.
    /// </summary>
    public void Record(
        IReadOnlyDictionary<string, object> sampleParameters,
        string exceptionType,
        string exceptionMessage,
        string stackTrace)
    {
        lock (_lock)
        {
            var key = (exceptionType, stackTrace);
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.OccurrenceCount++;
            }
            else if (_entries.Count < _capacity)
            {
                _entries[key] = new FailureEntry
                {
                    ExceptionType = exceptionType,
                    ExceptionMessage = exceptionMessage,
                    StackTrace = stackTrace,
                    SampleParameters = sampleParameters,
                    OccurrenceCount = 1,
                };
            }
        }
    }

    /// <summary>
    /// Records a trial timeout. All timeouts collapse into a single synthetic entry
    /// with ExceptionType "TrialTimeout" and empty stack trace.
    /// </summary>
    public void RecordTimeout(IReadOnlyDictionary<string, object> sampleParameters, TimeSpan timeout)
    {
        Record(sampleParameters, "TrialTimeout", $"Trial timed out after {timeout}", string.Empty);
    }

    /// <summary>
    /// Drains all entries and returns them as <see cref="FailedTrialRecord"/> instances
    /// stamped with the given optimization run ID. Call only after all producers are done.
    /// </summary>
    public List<FailedTrialRecord> Drain(Guid optimizationRunId)
    {
        var results = new List<FailedTrialRecord>(_entries.Count);
        foreach (var entry in _entries.Values)
        {
            results.Add(new FailedTrialRecord
            {
                Id = Guid.NewGuid(),
                OptimizationRunId = optimizationRunId,
                ExceptionType = entry.ExceptionType,
                ExceptionMessage = entry.ExceptionMessage,
                StackTrace = entry.StackTrace,
                SampleParameters = entry.SampleParameters,
                OccurrenceCount = entry.OccurrenceCount,
            });
        }
        return results;
    }

    private sealed class FailureEntry
    {
        public required string ExceptionType { get; init; }
        public required string ExceptionMessage { get; init; }
        public required string StackTrace { get; init; }
        public required IReadOnlyDictionary<string, object> SampleParameters { get; init; }
        public long OccurrenceCount { get; set; }
    }
}
