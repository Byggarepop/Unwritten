using ConventionSense.Core;
using ConventionSense.Git;
using ConventionSense.Storage;

namespace ConventionSense.Integration.Tests;

public class GitIndexingTests : IDisposable
{
    private readonly SyntheticRepo _repo = new();
    private readonly GitTransactionSource _source = new(new GitRunner());
    private readonly IndexManager _manager;

    public GitIndexingTests()
    {
        _manager = new IndexManager(_source);
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void BuildsIndexFromHistory()
    {
        for (int i = 0; i < 3; i++)
        {
            _repo.Commit("src/a.cs", "src/b.cs");
        }

        _repo.Commit("src/a.cs");

        var persisted = _manager.GetUpToDate(_repo.Path);

        Assert.Equal(_repo.Head(), persisted.HeadSha);
        Assert.Equal(4, persisted.Index.TransactionCount);
        Assert.Equal(4, persisted.Index.GetEntityCount("src/a.cs"));
        Assert.Equal(3, persisted.Index.GetEntityCount("src/b.cs"));
        Assert.Equal(3, persisted.Index.GetPair("src/a.cs", "src/b.cs")!.Count);
        Assert.True(File.Exists(IndexStore.GetIndexPath(_repo.Path)));
    }

    [Fact]
    public void MergeCommitsAreExcluded()
    {
        string baseSha = _repo.Commit("a.txt");
        _repo.CreateBranch("feature", baseSha);
        _repo.Commit("feature.txt");
        _repo.Checkout("main");
        _repo.Commit("main.txt");
        _repo.Merge("feature");

        var persisted = _manager.GetUpToDate(_repo.Path);

        // 3 real commits; the merge commit is not a transaction.
        Assert.Equal(3, persisted.Index.TransactionCount);
    }

    [Fact]
    public void MegaCommitsAreExcluded()
    {
        _repo.Commit("normal1.txt", "normal2.txt");
        _repo.Commit([.. Enumerable.Range(0, 31).Select(i => $"bulk/file{i}.txt")]);

        var persisted = _manager.GetUpToDate(_repo.Path);

        Assert.Equal(1, persisted.Index.TransactionCount);
        Assert.Equal(0, persisted.Index.GetEntityCount("bulk/file0.txt"));
    }

    [Fact]
    public void IncrementalUpdateOnlyIngestsNewCommits()
    {
        _repo.Commit("a.txt", "b.txt");
        var first = _manager.GetUpToDate(_repo.Path);
        Assert.Equal(1, first.Index.TransactionCount);

        _repo.Commit("a.txt", "b.txt");
        _repo.Commit("a.txt");

        // Fresh manager forces the load-from-disk + incremental path.
        var freshManager = new IndexManager(_source);
        var updated = freshManager.GetUpToDate(_repo.Path);

        Assert.Equal(3, updated.Index.TransactionCount);
        Assert.Equal(3, updated.Index.GetEntityCount("a.txt"));
        Assert.Equal(2, updated.Index.GetPair("a.txt", "b.txt")!.Count);
        Assert.Equal(_repo.Head(), updated.HeadSha);
    }

    [Fact]
    public void UnreachableIndexedShaFallsBackToFullRebuild()
    {
        _repo.Commit("a.txt");
        var persisted = _manager.GetUpToDate(_repo.Path);

        // Simulate a rewritten history: persist an index pointing at a bogus SHA.
        IndexStore.Save(_repo.Path, persisted.Index, new string('0', 40));
        _repo.Commit("a.txt", "b.txt");

        var rebuilt = new IndexManager(_source).GetUpToDate(_repo.Path);

        Assert.Equal(2, rebuilt.Index.TransactionCount);
        Assert.Equal(2, rebuilt.Index.GetEntityCount("a.txt"));
    }

    [Fact]
    public void PersistedIndexRoundTripsThroughDisk()
    {
        for (int i = 0; i < 12; i++)
        {
            _repo.Commit("core.cs", "core.tests.cs");
        }

        var built = _manager.GetUpToDate(_repo.Path);
        var loaded = IndexStore.Load(_repo.Path);

        Assert.NotNull(loaded);
        Assert.Equal(built.HeadSha, loaded.HeadSha);
        Assert.Equal(built.Index.TransactionCount, loaded.Index.TransactionCount);
        Assert.Equal(
            built.Index.GetPair("core.cs", "core.tests.cs")!.Examples,
            loaded.Index.GetPair("core.cs", "core.tests.cs")!.Examples);
    }

    [Fact]
    public void FastHeadResolutionMatchesGit()
    {
        _repo.Commit("a.txt");
        Assert.Equal(_repo.Head(), GitHead.TryResolve(_repo.Path));

        _repo.Commit("b.txt");
        Assert.Equal(_repo.Head(), GitHead.TryResolve(_repo.Path));

        // Packed refs are the other storage location a branch tip can live in.
        _repo.Git("pack-refs", "--all");
        Assert.Equal(_repo.Head(), GitHead.TryResolve(_repo.Path));
    }

    [Fact]
    public void FastHeadResolutionRefusesNonRepos()
    {
        Assert.Null(GitHead.TryResolve(Path.GetTempPath()));
    }

    [Fact]
    public void TransactionsTouchingAFileIncludeTheirFullFileList()
    {
        _repo.Commit("a.txt", "b.txt");
        _repo.Commit("a.txt");
        _repo.Commit("unrelated.txt");

        var touching = _source.LoadTransactionsTouching(_repo.Path, "a.txt");

        Assert.Equal(2, touching.Count);
        // Newest first; the co-change commit must list b.txt too (--full-diff),
        // otherwise "changed alone" detection is impossible.
        Assert.Equal(["a.txt"], touching[0].Entities);
        Assert.Equal(["a.txt", "b.txt"], touching[1].Entities.Order());
    }

    [Fact]
    public void RulesEmergeFromRepeatedCoChanges()
    {
        // 12 co-changes of model+schema, 1 model-alone commit: wilson_lb(12,13) ≈ 0.67.
        for (int i = 0; i < 12; i++)
        {
            _repo.Commit("src/model.cs", "db/schema.sql");
        }

        _repo.Commit("src/model.cs");

        var persisted = _manager.GetUpToDate(_repo.Path);
        var holes = RuleEngine.FindHoles(persisted.Index, ["src/model.cs"], minConfidence: 0.6);

        var hole = Assert.Single(holes);
        Assert.Equal("db/schema.sql", hole.Hole);
        Assert.Equal(12, hole.CoChanges);
        Assert.Equal(13, hole.TotalChanges);
        Assert.All(hole.ExampleTransactions, e => Assert.False(string.IsNullOrEmpty(e.Label)));
    }
}
