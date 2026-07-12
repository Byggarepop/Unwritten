using Unwritten.Git;
using Unwritten.Storage;
using Unwritten.Tool;

namespace Unwritten.Integration.Tests;

/// <summary>
/// Bounded rule mutes: an ignore suppresses (visibly) instead of blocking,
/// expires after the trigger has changed N more times, and never survives
/// --strict. Permanent ignores don't exist by design.
/// </summary>
public class IgnoreRuleTests : IDisposable
{
    private readonly SyntheticRepo _repo = new();
    private readonly GitTransactionSource _source = new(new GitRunner());
    private readonly IndexManager _manager;

    public IgnoreRuleTests()
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

    private int RunIgnore(out string output, params string[] args)
    {
        using var writer = new StringWriter();
        int exitCode = IgnoreCommand.Run(
            [.. args, "--repo", _repo.Path], _manager, writer);
        output = writer.ToString();
        return exitCode;
    }

    /// <summary>30 co-changes + 1 alone: wilson(30,31) ≈ 0.81, safely above the 0.7 fail floor.</summary>
    private void BuildStrongCouplingWithHole()
    {
        for (int i = 0; i < 30; i++)
        {
            _repo.Commit("api.txt", "api.contract.txt");
        }

        _repo.Commit("api.txt");
    }

    [Fact]
    public void IgnoredHoleIsSuppressedNotDropped()
    {
        BuildStrongCouplingWithHole();
        Assert.Equal(1, RunCheck(out _, "api.txt"));

        Assert.Equal(0, RunIgnore(out string ignoreOutput, "api.txt", "api.contract.txt", "--for", "5"));
        Assert.Contains("next 5 change(s)", ignoreOutput);

        int exitCode = RunCheck(out string output, "api.txt");

        Assert.Equal(0, exitCode);
        Assert.Contains("suppressed: api.contract.txt", output);
        Assert.Contains("expires after 5 more change(s)", output);
    }

    [Fact]
    public void IgnoreExpiresAfterTheTriggerChangesEnoughTimes()
    {
        BuildStrongCouplingWithHole();
        RunIgnore(out _, "api.txt", "api.contract.txt", "--for", "2");

        _repo.Commit("api.txt");
        _repo.Commit("api.txt");

        int exitCode = RunCheck(out string output, "api.txt");

        Assert.Equal(1, exitCode);
        Assert.Contains("api.contract.txt", output);
        Assert.DoesNotContain("suppressed:", output);
    }

    [Fact]
    public void StrictOverridesIgnores()
    {
        BuildStrongCouplingWithHole();
        RunIgnore(out _, "api.txt", "api.contract.txt", "--for", "5");

        Assert.Equal(1, RunCheck(out _, "api.txt", "--strict"));
    }

    [Fact]
    public void ListShowsRemainingBudgetAndRemoveRestoresTheAlert()
    {
        BuildStrongCouplingWithHole();
        RunIgnore(out _, "api.txt", "api.contract.txt", "--for", "5", "--note", "docs-only pairing");

        Assert.Equal(0, RunIgnore(out string listOutput, "--list"));
        Assert.Contains("api.txt -> api.contract.txt", listOutput);
        Assert.Contains("5 change(s)", listOutput);
        Assert.Contains("docs-only pairing", listOutput);

        Assert.Equal(0, RunIgnore(out _, "--remove", "api.txt", "api.contract.txt"));
        Assert.Equal(1, RunCheck(out _, "api.txt"));
    }

    [Fact]
    public void RemovingAnUnknownIgnoreSaysSo()
    {
        BuildStrongCouplingWithHole();

        Assert.Equal(1, RunIgnore(out string output, "--remove", "api.txt", "nope.txt"));
        Assert.Contains("No ignore found", output);
    }

    [Fact]
    public void CorruptIgnoresFileFailsTowardAlerting()
    {
        BuildStrongCouplingWithHole();
        Directory.CreateDirectory(Path.Combine(_repo.Path, ".unwritten"));
        File.WriteAllText(IgnoreStore.GetPath(_repo.Path), "{ not valid");

        Assert.Equal(1, RunCheck(out _, "api.txt"));
    }
}
