using AlgoTradeForge.Application.Optimization;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Optimization;

public sealed class FailedTrialCollectorTests
{
    private static IReadOnlyDictionary<string, object> MakeParams(string key = "p1", object? value = null) =>
        new Dictionary<string, object> { [key] = value ?? 42 };

    [Fact]
    public void New_signature_adds_entry()
    {
        var collector = new FailedTrialCollector(100);
        collector.Record(MakeParams(), "NullReferenceException", "Object ref not set", "at Foo.Bar()");

        var results = collector.Drain(Guid.NewGuid());
        Assert.Single(results);
        Assert.Equal("NullReferenceException", results[0].ExceptionType);
        Assert.Equal("Object ref not set", results[0].ExceptionMessage);
        Assert.Equal("at Foo.Bar()", results[0].StackTrace);
        Assert.Equal(1, results[0].OccurrenceCount);
    }

    [Fact]
    public void Duplicate_signature_increments_count()
    {
        var collector = new FailedTrialCollector(100);
        collector.Record(MakeParams("a", 1), "NullReferenceException", "msg1", "at Foo.Bar()");
        collector.Record(MakeParams("a", 2), "NullReferenceException", "msg2", "at Foo.Bar()");
        collector.Record(MakeParams("a", 3), "NullReferenceException", "msg3", "at Foo.Bar()");

        var results = collector.Drain(Guid.NewGuid());
        Assert.Single(results);
        Assert.Equal(3, results[0].OccurrenceCount);
        // First sample message is preserved
        Assert.Equal("msg1", results[0].ExceptionMessage);
    }

    [Fact]
    public void Different_type_same_stack_creates_two_entries()
    {
        var collector = new FailedTrialCollector(100);
        collector.Record(MakeParams(), "NullReferenceException", "msg", "at Foo.Bar()");
        collector.Record(MakeParams(), "ArgumentException", "msg", "at Foo.Bar()");

        var results = collector.Drain(Guid.NewGuid());
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Same_type_different_stack_creates_two_entries()
    {
        var collector = new FailedTrialCollector(100);
        collector.Record(MakeParams(), "NullReferenceException", "msg", "at Foo.Bar()");
        collector.Record(MakeParams(), "NullReferenceException", "msg", "at Baz.Qux()");

        var results = collector.Drain(Guid.NewGuid());
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void At_capacity_rejects_new_but_increments_existing()
    {
        var collector = new FailedTrialCollector(2);
        collector.Record(MakeParams(), "TypeA", "msg", "stack1");
        collector.Record(MakeParams(), "TypeB", "msg", "stack2");
        // At capacity — new signature rejected
        collector.Record(MakeParams(), "TypeC", "msg", "stack3");
        // Existing signature still increments
        collector.Record(MakeParams(), "TypeA", "msg", "stack1");

        var results = collector.Drain(Guid.NewGuid());
        Assert.Equal(2, results.Count);
        var typeA = results.First(r => r.ExceptionType == "TypeA");
        Assert.Equal(2, typeA.OccurrenceCount);
    }

    [Fact]
    public void Timeout_collapses_into_single_entry()
    {
        var collector = new FailedTrialCollector(100);
        var timeout = TimeSpan.FromSeconds(30);
        collector.RecordTimeout(MakeParams("a", 1), timeout);
        collector.RecordTimeout(MakeParams("a", 2), timeout);
        collector.RecordTimeout(MakeParams("a", 3), timeout);

        var results = collector.Drain(Guid.NewGuid());
        Assert.Single(results);
        Assert.Equal("TrialTimeout", results[0].ExceptionType);
        Assert.Equal(string.Empty, results[0].StackTrace);
        Assert.Equal(3, results[0].OccurrenceCount);
    }

    [Fact]
    public void Drain_assigns_run_id()
    {
        var collector = new FailedTrialCollector(100);
        collector.Record(MakeParams(), "Ex", "msg", "stack");

        var runId = Guid.NewGuid();
        var results = collector.Drain(runId);
        Assert.Single(results);
        Assert.Equal(runId, results[0].OptimizationRunId);
        Assert.NotEqual(Guid.Empty, results[0].Id);
    }

    [Fact]
    public void Empty_drain_returns_empty_list()
    {
        var collector = new FailedTrialCollector(100);
        var results = collector.Drain(Guid.NewGuid());
        Assert.Empty(results);
    }
}
