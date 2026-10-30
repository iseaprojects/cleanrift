using CleanRift.Core;
using Xunit;

namespace CleanRift.Tests;

public sealed class TraceLookupTests
{
    private static TraceInfo Trace(string id, string name) => new()
    {
        Id = id,
        DisplayName = name
    };

    [Fact]
    public void DirectIdWins()
    {
        var traces = new Dictionary<string, TraceInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = Trace("A", "Same"),
            ["B"] = Trace("B", "Same")
        };

        Assert.Same(traces["A"], TraceStore.FindFor(traces, "a", "Same"));
    }

    [Fact]
    public void AmbiguousDisplayNameIsNotUsed()
    {
        var traces = new Dictionary<string, TraceInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = Trace("A", "Same"),
            ["B"] = Trace("B", "Same")
        };

        Assert.Null(TraceStore.FindFor(traces, "missing", "Same"));
    }

    [Fact]
    public void UniqueDisplayNameIsUsed()
    {
        var traces = new Dictionary<string, TraceInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = Trace("A", "Example"),
            ["B"] = Trace("B", "Other")
        };

        Assert.Same(traces["A"], TraceStore.FindFor(traces, "missing", "example"));
    }
}
