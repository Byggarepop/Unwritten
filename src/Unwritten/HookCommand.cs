using System.Text.Json;
using System.Text.Json.Nodes;
using Unwritten.Git;
using Unwritten.Storage;

namespace Unwritten.Tool;

/// <summary>
/// <c>unwritten install-hook</c> — one-command setup of deterministic checks: a
/// git pre-commit hook, a Claude Code Stop hook, or both. Relying on an agent to
/// remember a tool is the weakest link in the chain; hooks make the check fire
/// every time. Also implements <c>unwritten hook stop</c>, the command the
/// Claude Code hook runs.
/// </summary>
public static class HookCommand
{
    private const string CheckCommandLine = "dotnet tool execute Unwritten --yes -- check --staged";
    private const string StopHookCommandLine = "dotnet tool execute Unwritten --yes -- hook stop";

    private const string PreCommitScript = """
        #!/bin/sh
        # Installed by 'unwritten install-hook --git'.
        exec dotnet tool execute Unwritten --yes -- check --staged
        """;

    public static int Install(string[] args, IndexManager indexManager, GitTransactionSource gitSource, TextWriter output)
    {
        bool git = false;
        bool claude = false;
        bool force = false;
        string repoPath = Directory.GetCurrentDirectory();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--git":
                    git = true;
                    break;
                case "--claude-code":
                    claude = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--repo" when i + 1 < args.Length:
                    repoPath = args[++i];
                    break;
                default:
                    output.WriteLine($"Unknown option: {args[i]}");
                    return 2;
            }
        }

        if (!git && !claude)
        {
            output.WriteLine("Nothing to install. Pass --git (pre-commit hook), --claude-code (Stop hook), or both.");
            return 2;
        }

        repoPath = indexManager.ResolveRepoRoot(repoPath);
        int result = 0;
        if (git)
        {
            result = Math.Max(result, InstallGitHook(repoPath, gitSource, force, output));
        }

        if (claude)
        {
            result = Math.Max(result, InstallClaudeCodeHook(repoPath, output));
        }

        return result;
    }

    /// <summary>
    /// Claude Code Stop-hook mode: checks everything uncommitted in the hook's
    /// cwd; exits 2 with the report on stderr (which Claude Code feeds back to
    /// the agent) when a hole reaches the fail floor. Fails open — never blocks
    /// the agent on infrastructure problems, and never re-blocks in the same
    /// turn (stop_hook_active).
    /// </summary>
    public static int Stop(IndexManager indexManager, GitTransactionSource gitSource, TextReader input, TextWriter error)
    {
        try
        {
            var (stopHookActive, cwd) = ReadPayload(input);
            if (stopHookActive)
            {
                return 0;
            }

            string repoPath = indexManager.ResolveRepoRoot(
                string.IsNullOrWhiteSpace(cwd) ? Directory.GetCurrentDirectory() : cwd);
            using var buffer = new StringWriter();
            int exitCode = CheckCommand.Run(["--repo", repoPath], indexManager, gitSource, buffer, logSource: "stop-hook");
            if (exitCode != 1)
            {
                return 0;
            }

            error.WriteLine("Unwritten found likely-missing companion changes in this session's edits:");
            error.WriteLine(buffer.ToString());
            error.WriteLine("Fix each hole or state briefly why it does not apply here (the evidence above and the explain_rule tool can help you judge).");
            error.WriteLine("If you judge a rule to be a persistently false pattern, tell the user — they can mute it with 'unwritten ignore <trigger> <hole> --for <n>'. That decision is theirs, not yours.");
            return 2;
        }
        catch (Exception)
        {
            // Deliberately broad: a hook crash surfaces as a cryptic blocked turn
            // for the agent. Whatever went wrong, not blocking is always the
            // right outcome here.
            return 0;
        }
    }

    private static (bool StopHookActive, string? Cwd) ReadPayload(TextReader input)
    {
        try
        {
            string payload = input.ReadToEnd();
            if (string.IsNullOrWhiteSpace(payload))
            {
                return (false, null);
            }

            using var document = JsonDocument.Parse(payload);
            bool active = document.RootElement.TryGetProperty("stop_hook_active", out var activeValue) &&
                activeValue.ValueKind == JsonValueKind.True;
            string? cwd = document.RootElement.TryGetProperty("cwd", out var cwdValue) &&
                cwdValue.ValueKind == JsonValueKind.String
                    ? cwdValue.GetString()
                    : null;
            return (active, cwd);
        }
        catch (JsonException)
        {
            return (false, null);
        }
    }

    private static int InstallGitHook(string repoPath, GitTransactionSource gitSource, bool force, TextWriter output)
    {
        string hooksDirectory = gitSource.GetHooksPath(repoPath);
        Directory.CreateDirectory(hooksDirectory);
        string hookPath = Path.Combine(hooksDirectory, "pre-commit");

        if (File.Exists(hookPath) && !force)
        {
            if (File.ReadAllText(hookPath).Contains("unwritten", StringComparison.OrdinalIgnoreCase))
            {
                output.WriteLine($"pre-commit hook already runs unwritten: {hookPath}");
                return 0;
            }

            output.WriteLine($"A pre-commit hook already exists: {hookPath}");
            output.WriteLine("Add this line to it yourself, or re-run with --force to overwrite:");
            output.WriteLine($"  {CheckCommandLine}");
            return 1;
        }

        File.WriteAllText(hookPath, PreCommitScript + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        output.WriteLine($"Installed git pre-commit hook: {hookPath}");
        return 0;
    }

    private static int InstallClaudeCodeHook(string repoPath, TextWriter output)
    {
        string settingsPath = Path.Combine(repoPath, ".claude", "settings.json");
        JsonObject root;
        if (File.Exists(settingsPath))
        {
            try
            {
                root = JsonNode.Parse(
                        File.ReadAllText(settingsPath),
                        documentOptions: new JsonDocumentOptions
                        {
                            CommentHandling = JsonCommentHandling.Skip,
                            AllowTrailingCommas = true,
                        }) as JsonObject
                    ?? throw new ConfigException($"{settingsPath} is not a JSON object; add the hook manually.");
            }
            catch (JsonException ex)
            {
                throw new ConfigException($"{settingsPath} is not valid JSON ({ex.Message}); fix it or add the hook manually.");
            }
        }
        else
        {
            root = new JsonObject();
        }

        if (root["hooks"] is not JsonObject hooks)
        {
            root["hooks"] = hooks = new JsonObject();
        }

        if (hooks["Stop"] is not JsonArray stop)
        {
            hooks["Stop"] = stop = new JsonArray();
        }

        bool installed = stop.OfType<JsonObject>().Any(group =>
            group["hooks"] is JsonArray entries && entries.OfType<JsonObject>().Any(entry =>
                (string?)entry["command"] is { } command &&
                command.Contains("unwritten", StringComparison.OrdinalIgnoreCase) &&
                command.Contains("hook stop", StringComparison.OrdinalIgnoreCase)));

        if (installed)
        {
            output.WriteLine($"Claude Code Stop hook already present: {settingsPath}");
            return 0;
        }

        stop.Add(new JsonObject
        {
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = StopHookCommandLine,
            }),
        });

        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        output.WriteLine($"Installed Claude Code Stop hook: {settingsPath}");
        output.WriteLine("When the agent finishes a turn with a failing hole, the report is fed back so it can fix or justify it.");
        return 0;
    }
}
