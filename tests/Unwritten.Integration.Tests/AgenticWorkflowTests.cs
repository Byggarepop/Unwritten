using Unwritten.Git;
using Unwritten.Storage;
using Unwritten.Tool;

namespace Unwritten.Integration.Tests;

/// <summary>
/// The scenarios that make or break the tool inside a coding agent's loop:
/// commit-as-you-go sessions (baseRef), auto-detection of the changed set, and
/// honest "no data" reporting for files without history.
/// </summary>
public class AgenticWorkflowTests : IDisposable
{
    private readonly SyntheticRepo _repo = new();
    private readonly GitTransactionSource _source = new(new GitRunner());
    private readonly IndexManager _manager;

    public AgenticWorkflowTests()
    {
        _manager = new IndexManager(_source);
    }

    public void Dispose() => _repo.Dispose();

    private int RunCheck(out string output, params string[] args)
    {
        using var writer = new StringWriter();
        int exitCode = CheckCommand.Run(
            [.. args, "--repo", _repo.Path], _manager, _source, writer);
        output = writer.ToString();
        return exitCode;
    }

    private static string ApiSource(int i) =>
        $$"""
        namespace A;
        public class Api
        {
            public int Version() => {{i}};
        }
        """;

    private static string ContractSource(int i) =>
        $$"""
        namespace A;
        public class ApiContract
        {
            public int Expected() => {{i}};
        }
        """;

    /// <summary>15 co-changes of api/contract with valid C# content on both sides.</summary>
    private void BuildCsCoupling()
    {
        for (int i = 1; i <= 15; i++)
        {
            _repo.CommitContents($"api change {i}", new Dictionary<string, string>
            {
                ["api.cs"] = ApiSource(i),
                ["api.contract.cs"] = ContractSource(i),
            });
        }
    }

    private void BuildTxtCoupling()
    {
        for (int i = 0; i < 15; i++)
        {
            _repo.Commit("api.txt", "api.contract.txt");
        }
    }

    [Fact]
    public void CommittedCsWorkIsNotSuppressedAsCosmetic()
    {
        BuildCsCoupling();

        // The agent commits its work — api.cs without the contract — and THEN
        // checks. Working tree now equals HEAD; identical content must read as
        // "no visible edit" (fail open), never as a cosmetic edit.
        _repo.CommitContents("forgot the contract", new Dictionary<string, string>
        {
            ["api.cs"] = ApiSource(99),
        });

        int exitCode = RunCheck(out string output, "api.cs", "--fail-at", "0.6");

        Assert.Equal(1, exitCode);
        Assert.Contains("api.contract.cs", output);
        Assert.DoesNotContain("suppressed:", output);
    }

    [Fact]
    public void BaseRefSeesCommittedChanges()
    {
        BuildTxtCoupling();
        string baseSha = _repo.Head().Trim();

        _repo.Commit("api.txt"); // committed mid-session, contract forgotten

        // No file list: --base auto-detects everything changed since the base.
        int exitCode = RunCheck(out string output, "--base", baseSha, "--fail-at", "0.6");

        Assert.Equal(1, exitCode);
        Assert.Contains("api.contract.txt", output);
    }

    [Fact]
    public void AutoDetectsUncommittedChanges()
    {
        BuildTxtCoupling();
        _repo.WriteFile("api.txt", "uncommitted edit\n");

        int exitCode = RunCheck(out string output);

        Assert.Equal(1, exitCode);
        Assert.Contains("api.contract.txt", output);
    }

    [Fact]
    public void CleanTreeReportsNothingToCheck()
    {
        BuildTxtCoupling();

        int exitCode = RunCheck(out string output);

        Assert.Equal(0, exitCode);
        Assert.Contains("nothing to check", output);
    }

    [Fact]
    public void UnknownFileGetsANoDataNote()
    {
        _repo.Commit("a.txt");
        _repo.Commit("a.txt");

        int exitCode = RunCheck(out string output, "brand-new.txt");

        Assert.Equal(0, exitCode);
        Assert.Contains("no history in the index", output);
    }

    [Fact]
    public void ThinHistoryGetsANote()
    {
        _repo.Commit("a.txt");
        _repo.Commit("a.txt");

        int exitCode = RunCheck(out string output, "a.txt");

        Assert.Equal(0, exitCode);
        Assert.Contains("only 2 historical change(s)", output);
    }

    [Fact]
    public void SubdirectoryRepoPathResolvesToRoot()
    {
        for (int i = 0; i < 12; i++)
        {
            _repo.Commit("src/a.txt", "src/b.txt");
        }

        using var writer = new StringWriter();
        int exitCode = CheckCommand.Run(
            ["src/a.txt", "--repo", Path.Combine(_repo.Path, "src")], _manager, _source, writer);

        Assert.Equal(1, exitCode);
        Assert.Contains("src/b.txt", writer.ToString());
        Assert.True(File.Exists(IndexStore.GetIndexPath(_repo.Path)), "index must live at the repo root");
    }
}
