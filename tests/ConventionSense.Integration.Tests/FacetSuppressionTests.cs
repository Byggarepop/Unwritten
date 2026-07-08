using ConventionSense.Core;
using ConventionSense.Git;
using ConventionSense.Storage;
using ConventionSense.Tool;

namespace ConventionSense.Integration.Tests;

/// <summary>
/// End-to-end replica of the validated real-world case: a JSON file whose
/// "version" key predicts a companion change while "description" edits do not.
/// </summary>
public class FacetSuppressionTests : IDisposable
{
    private readonly SyntheticRepo _repo = new();
    private readonly GitTransactionSource _source = new(new GitRunner());
    private readonly IndexManager _manager;

    private string _version = "0";
    private string _description = "initial";

    public FacetSuppressionTests()
    {
        _manager = new IndexManager(_source);
    }

    public void Dispose() => _repo.Dispose();

    private string AJson() =>
        $$"""{ "version": "{{_version}}", "description": "{{_description}}" }""";

    private void CommitVersionBump(int i)
    {
        _version = $"1.0.{i}";
        _repo.CommitContents($"bump to {_version}", new Dictionary<string, string>
        {
            ["a.json"] = AJson(),
            ["b.txt"] = $"companion for {_version}\n",
        });
    }

    private void CommitDescriptionEdit(int i, bool alsoCompanion = false)
    {
        _description = $"description {i}";
        var contents = new Dictionary<string, string> { ["a.json"] = AJson() };
        if (alsoCompanion)
        {
            contents["b.txt"] = $"companion touched during description {i}\n";
        }

        _repo.CommitContents($"describe {i}", contents);
    }

    /// <summary>
    /// 18 version bumps co-changing b.txt (the first creates a.json, so 17 facet
    /// observations), 5 description edits (1 co-changing):
    /// file-level a.json→b.txt ≈ 0.63 (a real rule), facet "version" 17/17
    /// (predictive), facet "description" 1/5 ≈ 0.04 (non-predictive).
    /// </summary>
    private void BuildValidatedCaseHistory()
    {
        for (int i = 1; i <= 12; i++)
        {
            CommitVersionBump(i);
        }

        CommitDescriptionEdit(1, alsoCompanion: true);
        for (int i = 2; i <= 5; i++)
        {
            CommitDescriptionEdit(i);
        }

        for (int i = 13; i <= 18; i++)
        {
            CommitVersionBump(i);
        }
    }

    private int RunCheck(out string output, params string[] args)
    {
        using var writer = new StringWriter();
        int exitCode = CheckCommand.Run(
            [.. args, "--repo", _repo.Path, "--fail-at", "0.6"], _manager, _source, writer);
        output = writer.ToString();
        return exitCode;
    }

    [Fact]
    public void TrainingProducesExpectedFacetStats()
    {
        BuildValidatedCaseHistory();

        var index = _manager.GetUpToDate(_repo.Path).Index;
        var stats = index.GetFacetStats("a.json", "b.txt");

        Assert.NotNull(stats);
        Assert.Equal(17, stats["version"].Count);
        Assert.Equal(17, stats["version"].CoChanges);
        Assert.Equal(5, stats["description"].Count);
        Assert.Equal(1, stats["description"].CoChanges);
    }

    [Fact]
    public void FacetStatsSurviveDiskRoundTrip()
    {
        BuildValidatedCaseHistory();
        _manager.GetUpToDate(_repo.Path);

        var loaded = IndexStore.Load(_repo.Path)!;
        var stats = loaded.Index.GetFacetStats("a.json", "b.txt");

        Assert.NotNull(stats);
        Assert.Equal(17, stats["version"].Count);
    }

    [Fact]
    public void DescriptionOnlyEditIsSuppressed()
    {
        BuildValidatedCaseHistory();
        _repo.WriteFile("a.json", AJson().Replace(_description, "a purely cosmetic reword"));

        int exitCode = RunCheck(out string output, "a.json");

        Assert.Equal(0, exitCode);
        Assert.Contains("suppressed: b.txt", output);
        Assert.Contains("description", output);
        Assert.DoesNotContain("FAIL", output);
    }

    [Fact]
    public void VersionEditStillTriggers()
    {
        BuildValidatedCaseHistory();
        _repo.WriteFile("a.json", AJson().Replace(_version, "2.0.0"));

        int exitCode = RunCheck(out string output, "a.json");

        Assert.Equal(1, exitCode);
        Assert.Contains("b.txt", output);
        Assert.Contains("FAIL", output);
    }

    [Fact]
    public void UnknownKeyEditFailsOpen()
    {
        BuildValidatedCaseHistory();
        _repo.WriteFile("a.json",
            $$"""{ "version": "{{_version}}", "description": "{{_description}}", "brandNewKey": true }""");

        int exitCode = RunCheck(out _, "a.json");

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void StrictFlagIgnoresSuppression()
    {
        BuildValidatedCaseHistory();
        _repo.WriteFile("a.json", AJson().Replace(_description, "cosmetic"));

        int exitCode = RunCheck(out string output, "a.json", "--strict");

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("suppressed:", output);
    }

    [Fact]
    public void StagedDescriptionEditIsSuppressed()
    {
        BuildValidatedCaseHistory();
        _repo.WriteFile("a.json", AJson().Replace(_description, "cosmetic staged edit"));
        _repo.StageFile("a.json");

        int exitCode = RunCheck(out string output, "--staged");

        Assert.Equal(0, exitCode);
        Assert.Contains("suppressed: b.txt", output);
    }

    [Fact]
    public void IncrementalUpdateExtendsFacetStats()
    {
        BuildValidatedCaseHistory();
        _manager.GetUpToDate(_repo.Path);

        CommitVersionBump(19);

        var fresh = new IndexManager(_source); // force load-from-disk + incremental path
        var stats = fresh.GetUpToDate(_repo.Path).Index.GetFacetStats("a.json", "b.txt");

        Assert.Equal(18, stats!["version"].Count);
        Assert.Equal(18, stats["version"].CoChanges);
    }

    [Fact]
    public void RuleCrossingTheFloorGetsBackfilledInline()
    {
        // Only 8 co-changes: below MinSupport (10), so no rule and no training.
        for (int i = 1; i <= 8; i++)
        {
            CommitVersionBump(i);
        }

        var before = _manager.GetUpToDate(_repo.Path).Index;
        Assert.Null(before.GetFacetStats("a.json", "b.txt"));

        // Ten more co-changes push the pair over the candidate floor; the next
        // incremental update must backfill facet stats from FULL history
        // (17 observations — the first commit created a.json, no parent to diff).
        for (int i = 9; i <= 18; i++)
        {
            CommitVersionBump(i);
        }

        var after = new IndexManager(_source).GetUpToDate(_repo.Path).Index;
        var stats = after.GetFacetStats("a.json", "b.txt");

        Assert.NotNull(stats);
        Assert.Equal(17, stats["version"].Count);
    }
}
