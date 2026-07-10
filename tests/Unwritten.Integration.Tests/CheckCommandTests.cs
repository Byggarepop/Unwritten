using Unwritten.Git;
using Unwritten.Storage;
using Unwritten.Tool;

namespace Unwritten.Integration.Tests;

public class CheckCommandTests : IDisposable
{
    private readonly SyntheticRepo _repo = new();
    private readonly GitTransactionSource _source = new(new GitRunner());
    private readonly IndexManager _manager;

    public CheckCommandTests()
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

    private void BuildStrongCoupling()
    {
        // 15 co-changes, 1 alone: wilson_lb(15,16) ≈ 0.72, above the 0.7 fail floor.
        for (int i = 0; i < 15; i++)
        {
            _repo.Commit("api.cs", "api.contract.cs");
        }

        _repo.Commit("api.cs");
    }

    [Fact]
    public void ExitsZeroWhenNoHoles()
    {
        BuildStrongCoupling();

        int exitCode = RunCheck(out string output, "api.cs", "api.contract.cs");

        Assert.Equal(0, exitCode);
        Assert.Contains("No holes", output);
    }

    [Fact]
    public void ExitsOneWhenAHoleReachesTheFailFloor()
    {
        BuildStrongCoupling();

        int exitCode = RunCheck(out string output, "api.cs");

        Assert.Equal(1, exitCode);
        Assert.Contains("api.contract.cs", output);
        Assert.Contains("FAIL", output);
    }

    [Fact]
    public void ReportsButPassesForHolesBetweenFloors()
    {
        BuildStrongCoupling();

        // Raise the fail floor above the rule's confidence: report, but exit 0.
        int exitCode = RunCheck(out string output, "api.cs", "--fail-at", "0.99");

        Assert.Equal(0, exitCode);
        Assert.Contains("api.contract.cs", output);
        Assert.DoesNotContain("FAIL", output);
    }

    [Fact]
    public void ChecksStagedFiles()
    {
        BuildStrongCoupling();
        _repo.Stage("api.cs");

        int exitCode = RunCheck(out string output, "--staged");

        Assert.Equal(1, exitCode);
        Assert.Contains("api.contract.cs", output);
    }

    [Fact]
    public void ExitsZeroWithNothingStaged()
    {
        BuildStrongCoupling();

        int exitCode = RunCheck(out string output, "--staged");

        Assert.Equal(0, exitCode);
        Assert.Contains("Nothing staged", output);
    }

    [Fact]
    public void RejectsUnknownOptions()
    {
        int exitCode = RunCheck(out _, "--bogus");

        Assert.Equal(2, exitCode);
    }
}
