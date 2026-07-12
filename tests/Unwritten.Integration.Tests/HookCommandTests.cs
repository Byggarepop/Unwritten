using System.Text.Json;
using Unwritten.Git;
using Unwritten.Storage;
using Unwritten.Tool;

namespace Unwritten.Integration.Tests;

public class HookCommandTests : IDisposable
{
    private readonly SyntheticRepo _repo = new();
    private readonly GitTransactionSource _source = new(new GitRunner());
    private readonly IndexManager _manager;

    public HookCommandTests()
    {
        _manager = new IndexManager(_source);
    }

    public void Dispose() => _repo.Dispose();

    private string PreCommitPath => Path.Combine(_repo.Path, ".git", "hooks", "pre-commit");

    private string SettingsPath => Path.Combine(_repo.Path, ".claude", "settings.json");

    private int Install(out string output, params string[] args)
    {
        using var writer = new StringWriter();
        int exitCode = HookCommand.Install(
            [.. args, "--repo", _repo.Path], _manager, _source, writer);
        output = writer.ToString();
        return exitCode;
    }

    private int Stop(out string error, string? payload = null)
    {
        using var writer = new StringWriter();
        payload ??= JsonSerializer.Serialize(new { cwd = _repo.Path });
        int exitCode = HookCommand.Stop(_manager, _source, new StringReader(payload), writer);
        error = writer.ToString();
        return exitCode;
    }

    private void BuildFailingHoleState()
    {
        for (int i = 0; i < 15; i++)
        {
            _repo.Commit("api.txt", "api.contract.txt");
        }

        _repo.WriteFile("api.txt", "uncommitted edit without the contract\n");
    }

    [Fact]
    public void InstallsGitPreCommitHookIdempotently()
    {
        Assert.Equal(0, Install(out string output, "--git"));
        Assert.Contains("Installed", output);
        Assert.Contains("unwritten", File.ReadAllText(PreCommitPath));

        Assert.Equal(0, Install(out string second, "--git"));
        Assert.Contains("already", second);
    }

    [Fact]
    public void RefusesToOverwriteAForeignPreCommitHook()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PreCommitPath)!);
        File.WriteAllText(PreCommitPath, "#!/bin/sh\necho existing hook\n");

        Assert.Equal(1, Install(out string output, "--git"));
        Assert.Contains("already exists", output);
        Assert.Contains("existing hook", File.ReadAllText(PreCommitPath));

        Assert.Equal(0, Install(out _, "--git", "--force"));
        Assert.Contains("unwritten", File.ReadAllText(PreCommitPath));
    }

    [Fact]
    public void InstallsClaudeCodeStopHookIdempotently()
    {
        Assert.Equal(0, Install(out _, "--claude-code"));
        string settings = File.ReadAllText(SettingsPath);
        Assert.Contains("hook stop", settings);

        Assert.Equal(0, Install(out string second, "--claude-code"));
        Assert.Contains("already", second);
        int occurrences = File.ReadAllText(SettingsPath).Split("hook stop").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void PreservesExistingClaudeCodeSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """{ "model": "opus" }""");

        Assert.Equal(0, Install(out _, "--claude-code"));

        using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.Equal("opus", document.RootElement.GetProperty("model").GetString());
        Assert.True(document.RootElement.TryGetProperty("hooks", out _));
    }

    [Fact]
    public void RequiresAtLeastOneTarget()
    {
        Assert.Equal(2, Install(out string output));
        Assert.Contains("--git", output);
    }

    [Fact]
    public void StopBlocksWithTheReportOnAFailingHole()
    {
        BuildFailingHoleState();

        int exitCode = Stop(out string error);

        Assert.Equal(2, exitCode);
        Assert.Contains("api.contract.txt", error);
    }

    [Fact]
    public void StopPassesOnACleanTree()
    {
        for (int i = 0; i < 3; i++)
        {
            _repo.Commit("a.txt");
        }

        Assert.Equal(0, Stop(out _));
    }

    [Fact]
    public void StopNeverReBlocksInTheSameTurn()
    {
        BuildFailingHoleState();

        string payload = JsonSerializer.Serialize(new { cwd = _repo.Path, stop_hook_active = true });
        Assert.Equal(0, Stop(out _, payload));
    }

    [Fact]
    public void StopFailsOpenOutsideARepository()
    {
        string payload = JsonSerializer.Serialize(new { cwd = Path.GetTempPath() });
        Assert.Equal(0, Stop(out _, payload));
    }
}
