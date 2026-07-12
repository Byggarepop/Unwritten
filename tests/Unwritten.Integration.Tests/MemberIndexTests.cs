using Unwritten.Git;
using Unwritten.Storage;
using Unwritten.Tool;

namespace Unwritten.Integration.Tests;

/// <summary>
/// End-to-end member-level indexing: a service method whose test method always
/// co-changes, detected at member granularity, plus cosmetic-edit suppression.
/// </summary>
public class MemberIndexTests : IDisposable
{
    private readonly SyntheticRepo _repo = new();
    private readonly GitTransactionSource _source = new(new GitRunner());
    private readonly IndexManager _manager;

    public MemberIndexTests()
    {
        _manager = new IndexManager(_source);
        Directory.CreateDirectory(Path.Combine(_repo.Path, ".unwritten"));
        File.WriteAllText(
            Path.Combine(_repo.Path, ".unwritten", "config.json"),
            """{ "memberLevel": true }""");
    }

    public void Dispose() => _repo.Dispose();

    private string SvcSource(int m1Version, int m2Version) =>
        $$"""
        namespace MyApp;
        public class Svc
        {
            public int M1() => {{m1Version}};
            public int M2() => {{m2Version}};
        }
        """;

    private string TestsSource(int t1Version) =>
        $$"""
        namespace MyApp;
        public class SvcTests
        {
            public int T1() => {{t1Version}};
        }
        """;

    /// <summary>
    /// 12 commits changing M1 + T1 together (the first also creates everything),
    /// 3 commits changing M2 alone. Member rule M1→T1: 12/12 co-changes ≈ 0.74.
    /// </summary>
    private void BuildHistory()
    {
        for (int i = 1; i <= 12; i++)
        {
            _repo.CommitContents($"m1 change {i}", new Dictionary<string, string>
            {
                ["Svc.cs"] = SvcSource(i, 0),
                ["SvcTests.cs"] = TestsSource(i),
            });
        }

        for (int i = 1; i <= 3; i++)
        {
            _repo.CommitContents($"m2 change {i}", new Dictionary<string, string>
            {
                ["Svc.cs"] = SvcSource(12, i),
            });
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
    public void MemberIndexLearnsMemberRules()
    {
        BuildHistory();

        var members = _manager.GetMembersUpToDate(_repo.Path);

        Assert.NotNull(members);
        Assert.Equal(12, members.Index.GetEntityCount("MyApp.Svc.M1/0"));
        Assert.Equal(12, members.Index.GetEntityCount("MyApp.SvcTests.T1/0"));
        Assert.Equal(3 + 1, members.Index.GetEntityCount("MyApp.Svc.M2/0")); // 3 changes + creation
        Assert.Equal(12, members.Index.GetPair("MyApp.Svc.M1/0", "MyApp.SvcTests.T1/0")!.Count);
        Assert.Equal("SvcTests.cs", members.Index.GetEntityLocation("MyApp.SvcTests.T1/0"));
        Assert.True(File.Exists(IndexStore.GetMemberIndexPath(_repo.Path)));
    }

    [Fact]
    public void MemberIndexIsNullWhenNotEnabled()
    {
        File.Delete(UnwrittenConfig.GetConfigPath(_repo.Path));
        _repo.Commit("a.txt");

        Assert.Null(_manager.GetMembersUpToDate(_repo.Path));
        Assert.False(File.Exists(IndexStore.GetMemberIndexPath(_repo.Path)));
    }

    [Fact]
    public void EditingMethodBodyFlagsItsTestMethodAsMemberHole()
    {
        BuildHistory();
        _repo.WriteFile("Svc.cs", SvcSource(99, 3)); // change M1 body only

        int exitCode = RunCheck(out string output, "Svc.cs");

        Assert.Equal(1, exitCode);
        Assert.Contains("member-level hole(s)", output);
        Assert.Contains("MyApp.SvcTests.T1/0", output);
        Assert.Contains("(SvcTests.cs)", output);
    }

    [Fact]
    public void EditingUncoupledMethodRaisesNoMemberHole()
    {
        BuildHistory();
        _repo.WriteFile("Svc.cs", SvcSource(12, 99)); // change M2 body only

        RunCheck(out string output, "Svc.cs");

        Assert.DoesNotContain("member-level", output);
    }

    [Fact]
    public void CommentOnlyEditSuppressesFileHoleAndRaisesNoMemberHole()
    {
        BuildHistory();
        // Same code, comment added: no member changed.
        _repo.WriteFile("Svc.cs", SvcSource(12, 3).Replace(
            "public class Svc", "// cosmetic comment\npublic class Svc"));

        int exitCode = RunCheck(out string output, "Svc.cs");

        // File-level rule Svc.cs→SvcTests.cs is real (12/15 ≈ 0.55... below 0.6)
        // so assert via a lowered floor where it does fire and gets suppressed.
        int exitLow = RunCheck(out string outputLow, "Svc.cs", "--min-confidence", "0.5");

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("member-level", output);
        Assert.Equal(0, exitLow);
        Assert.Contains("suppressed: SvcTests.cs", outputLow);
        Assert.Contains("changed no member", outputLow);
    }

    [Fact]
    public void MemberIndexUpdatesIncrementally()
    {
        BuildHistory();
        _manager.GetMembersUpToDate(_repo.Path);

        _repo.CommitContents("m1 change 13", new Dictionary<string, string>
        {
            ["Svc.cs"] = SvcSource(13, 3),
            ["SvcTests.cs"] = TestsSource(13),
        });

        var fresh = new IndexManager(_source); // force load-from-disk + incremental path
        var members = fresh.GetMembersUpToDate(_repo.Path);

        Assert.Equal(13, members!.Index.GetEntityCount("MyApp.Svc.M1/0"));
        Assert.Equal(13, members.Index.GetPair("MyApp.Svc.M1/0", "MyApp.SvcTests.T1/0")!.Count);
    }

    [Fact]
    public void HistoryWindowChangeTriggersAutomaticRebuild()
    {
        BuildHistory();
        Assert.Equal(15, _manager.GetMembersUpToDate(_repo.Path)!.Index.TransactionCount);

        // No manual reindex: the persisted member index must be invalidated by
        // the window change alone (same manager, HEAD unchanged).
        File.WriteAllText(
            Path.Combine(_repo.Path, ".unwritten", "config.json"),
            """{ "memberLevel": true, "memberHistoryWindow": 3 }""");

        Assert.Equal(3, _manager.GetMembersUpToDate(_repo.Path)!.Index.TransactionCount);
    }

    [Fact]
    public void HistoryWindowLimitsTraining()
    {
        BuildHistory();
        Directory.CreateDirectory(Path.Combine(_repo.Path, ".unwritten"));
        File.WriteAllText(
            Path.Combine(_repo.Path, ".unwritten", "config.json"),
            """{ "memberLevel": true, "memberHistoryWindow": 3 }""");

        var members = _manager.GetMembersUpToDate(_repo.Path);

        // Only the last 3 commits (the M2 changes) are in the window.
        Assert.Equal(3, members!.Index.TransactionCount);
        Assert.Equal(0, members.Index.GetEntityCount("MyApp.Svc.M1/0"));
        Assert.Equal(3, members.Index.GetEntityCount("MyApp.Svc.M2/0"));
    }
}
