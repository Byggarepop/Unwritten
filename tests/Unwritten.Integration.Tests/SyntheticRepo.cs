using System.Diagnostics;
using System.Text;

namespace Unwritten.Integration.Tests;

/// <summary>
/// A throwaway git repository in a temp directory with fully scripted commits,
/// so co-change counts in tests are deterministic.
/// </summary>
public sealed class SyntheticRepo : IDisposable
{
    private int _commitNumber;

    public SyntheticRepo()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "unwritten-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        Git("init", "--initial-branch=main");
        Git("config", "user.name", "Unwritten Tests");
        Git("config", "user.email", "tests@unwritten.invalid");
        Git("config", "commit.gpgsign", "false");
    }

    public string Path { get; }

    /// <summary>Writes new content to the given files and commits them.</summary>
    public string Commit(params string[] files) => CommitWithMessage($"commit {_commitNumber + 1}", files);

    public string CommitWithMessage(string message, string[] files)
    {
        _commitNumber++;
        foreach (var file in files)
        {
            string fullPath = System.IO.Path.Combine(Path, file.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.AppendAllText(fullPath, $"change {_commitNumber}\n");
        }

        Git(["add", "--", .. files]);
        Git("commit", "-m", message);
        return Head();
    }

    /// <summary>Commits files with exact content (for content-aware facet tests).</summary>
    public string CommitContents(string message, IReadOnlyDictionary<string, string> contents)
    {
        _commitNumber++;
        foreach (var (file, content) in contents)
        {
            string fullPath = System.IO.Path.Combine(Path, file.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        Git(["add", "--", .. contents.Keys]);
        Git("commit", "-m", message);
        return Head();
    }

    /// <summary>Writes exact content to a working-tree file without committing.</summary>
    public void WriteFile(string file, string content)
    {
        string fullPath = System.IO.Path.Combine(Path, file.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    public void StageFile(string file) => Git("add", "--", file);

    public void Stage(params string[] files)
    {
        _commitNumber++;
        foreach (var file in files)
        {
            string fullPath = System.IO.Path.Combine(Path, file);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.AppendAllText(fullPath, $"staged change {_commitNumber}\n");
        }

        Git(["add", "--", .. files]);
    }

    public string Head() => Git("rev-parse", "HEAD").Trim();

    public void CreateBranch(string name, string startPoint) => Git("checkout", "-b", name, startPoint);

    public void Checkout(string refName) => Git("checkout", refName);

    public void Merge(string branch) => Git("merge", "--no-ff", "-m", $"merge {branch}", branch);

    public string Git(params IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Fixed dates keep commit SHAs stable within a scripted sequence.
        string date = $"2026-01-01T{_commitNumber / 60:00}:{_commitNumber % 60:00}:00 +0000";
        startInfo.Environment["GIT_AUTHOR_DATE"] = date;
        startInfo.Environment["GIT_COMMITTER_DATE"] = date;

        using var process = Process.Start(startInfo)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"git {string.Join(' ', arguments)} timed out");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed ({process.ExitCode}): {stderr}");
        }

        return stdout;
    }

    public void Dispose()
    {
        try
        {
            var directory = new DirectoryInfo(Path);
            foreach (var info in directory.GetFileSystemInfos("*", SearchOption.AllDirectories))
            {
                info.Attributes = FileAttributes.Normal; // .git objects are read-only on Windows
            }

            directory.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Best effort — temp cleanup should never fail a test.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
