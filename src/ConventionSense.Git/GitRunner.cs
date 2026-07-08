using System.Diagnostics;
using System.Text;

namespace ConventionSense.Git;

/// <summary>Thrown when a git invocation fails (non-zero exit code or missing binary).</summary>
public sealed class GitException(string message) : Exception(message);

/// <summary>Thin wrapper around the git executable. No libgit2sharp by design.</summary>
public sealed class GitRunner
{
    /// <summary>Runs git with the given arguments and returns stdout. Throws <see cref="GitException"/> on failure.</summary>
    public string Run(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            // Stdin must be redirected (and closed below), not inherited: when this
            // process runs as an MCP server, its stdin is a pipe with a pending
            // protocol read, and a git.exe inheriting that handle deadlocks on startup.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Keep non-ASCII paths readable instead of octal-escaped and quoted.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.quotepath=false");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new GitException($"Failed to start git: {ex.Message}");
        }

        process.StandardInput.Close();
        // Drain stderr concurrently so neither pipe can fill up and block git.
        var stderrTask = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = stderrTask.GetAwaiter().GetResult();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new GitException(
                $"git {string.Join(' ', arguments)} exited with code {process.ExitCode}: {stderr.Trim()}");
        }

        return stdout;
    }
}
