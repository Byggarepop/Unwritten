using ConventionSense.Git;

namespace ConventionSense.Integration.Tests;

public class JsonFacetDiffTests
{
    private static IReadOnlySet<string>? Diff(string before, string after, int maxDepth = 3, int maxFacets = 64) =>
        JsonFacetDiff.ChangedFacets(before, after, maxDepth, maxFacets);

    [Fact]
    public void DetectsChangedTopLevelKey()
    {
        var changed = Diff("""{ "version": "1.0", "name": "x" }""", """{ "version": "1.1", "name": "x" }""");

        Assert.Equal(["version"], changed!);
    }

    [Fact]
    public void IdenticalDocumentsYieldEmptySet()
    {
        Assert.Empty(Diff("""{ "a": 1 }""", """{ "a": 1 }""")!);
    }

    [Fact]
    public void DetectsNestedKeysWithDotPaths()
    {
        var changed = Diff(
            """{ "info": { "version": "1.0", "author": "x" } }""",
            """{ "info": { "version": "2.0", "author": "x" } }""");

        Assert.Equal(["info.version"], changed!);
    }

    [Fact]
    public void AddedAndRemovedKeysCountAsChanges()
    {
        var changed = Diff("""{ "a": 1, "b": 2 }""", """{ "a": 1, "c": 3 }""");

        Assert.Equal(["b", "c"], changed!.Order());
    }

    [Fact]
    public void ArrayIndicesCollapse()
    {
        var changed = Diff(
            """{ "packages": [ { "name": "p1", "version": "1" }, { "name": "p2", "version": "1" } ] }""",
            """{ "packages": [ { "name": "p1", "version": "2" }, { "name": "p2", "version": "1" } ] }""");

        Assert.Equal(["packages[].version"], changed!);
    }

    [Fact]
    public void AppendedArrayElementsReportTheirKeys()
    {
        var changed = Diff(
            """{ "servers": [ { "name": "a" } ] }""",
            """{ "servers": [ { "name": "a" }, { "name": "b", "url": "u" } ] }""");

        Assert.Equal(["servers[].name", "servers[].url"], changed!.Order());
    }

    [Fact]
    public void DepthIsCappedWithRollup()
    {
        var changed = Diff(
            """{ "a": { "b": { "c": { "d": 1 } } } }""",
            """{ "a": { "b": { "c": { "d": 2 } } } }""",
            maxDepth: 3);

        // Change is at depth 4 (a.b.c.d) — rolls up to the depth-3 ancestor.
        Assert.Equal(["a.b.c"], changed!);
    }

    [Fact]
    public void KindChangeMarksThePath()
    {
        var changed = Diff("""{ "a": { "x": 1 } }""", """{ "a": [1] }""");

        Assert.Equal(["a"], changed!);
    }

    [Fact]
    public void RootScalarChangeReportsDollar()
    {
        Assert.Equal(["$"], Diff("1", "2")!);
    }

    [Fact]
    public void InvalidJsonIsUntrackable()
    {
        Assert.Null(Diff("{ not json", """{ "a": 1 }"""));
        Assert.Null(Diff("""{ "a": 1 }""", "also not json"));
    }

    [Fact]
    public void TooManyChangedFacetsIsUntrackable()
    {
        string before = "{" + string.Join(",", Enumerable.Range(0, 70).Select(i => $"\"k{i}\": 1")) + "}";
        string after = "{" + string.Join(",", Enumerable.Range(0, 70).Select(i => $"\"k{i}\": 2")) + "}";

        Assert.Null(Diff(before, after, maxFacets: 64));
    }

    [Fact]
    public void ToleratesCommentsAndTrailingCommas()
    {
        var changed = Diff(
            """
            {
              // a comment
              "a": 1,
            }
            """,
            """{ "a": 2 }""");

        Assert.Equal(["a"], changed!);
    }
}
