using System.Text.Json;
using Unwritten.Storage;

namespace Unwritten.Integration.Tests;

public class FindingsLogTests : IDisposable
{
    private readonly string _repo = Path.Combine(
        Path.GetTempPath(), "unwritten-findingslog", Path.GetRandomFileName());

    public void Dispose()
    {
        try
        {
            Directory.Delete(_repo, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    /// <summary>Splits the log into its pretty-printed entries (each starts with "{" at column 0).</summary>
    private static List<string> ReadEntries(string repoPath)
    {
        var entries = new List<string>();
        foreach (string line in File.ReadLines(FindingsLog.GetLogPath(repoPath)))
        {
            if (line == "{")
            {
                entries.Add(line);
            }
            else
            {
                entries[^1] += Environment.NewLine + line;
            }
        }

        return entries;
    }

    [Fact]
    public void AppendsOnePrettyPrintedEntryPerCall()
    {
        FindingsLog.Append(_repo, "cli", new { Holes = new[] { "docs/CHANGELOG.md" }, Failing = true });
        FindingsLog.Append(_repo, "mcp", new { Holes = Array.Empty<string>(), Failing = false });

        var entries = ReadEntries(_repo);

        Assert.Equal(2, entries.Count);
        using var first = JsonDocument.Parse(entries[0]);
        Assert.Equal("cli", first.RootElement.GetProperty("source").GetString());
        Assert.Equal("docs/CHANGELOG.md",
            first.RootElement.GetProperty("result").GetProperty("holes")[0].GetString());
        Assert.True(first.RootElement.TryGetProperty("timestamp", out _));
        using var second = JsonDocument.Parse(entries[1]);
        Assert.Equal("mcp", second.RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public void SelfGitignoresTheLogDirectory()
    {
        FindingsLog.Append(_repo, "cli", new { Failing = false });

        Assert.Equal("*\n", File.ReadAllText(Path.Combine(_repo, ".unwritten", ".gitignore")));
    }

    [Fact]
    public void TrimsOldestEntriesOnceTheCapIsExceeded()
    {
        // Each entry is ~1 KB, so ~1100 of them overshoot the 1 MB cap.
        string padding = new('x', 1000);
        for (int i = 0; i < 1100; i++)
        {
            FindingsLog.Append(_repo, "cli", new { Sequence = i, Padding = padding });
        }

        var entries = ReadEntries(_repo);

        Assert.True(new FileInfo(FindingsLog.GetLogPath(_repo)).Length <= 1024 * 1024);
        Assert.NotEmpty(entries);

        // The newest entry survives; the oldest was trimmed away.
        using var last = JsonDocument.Parse(entries[^1]);
        Assert.Equal(1099, last.RootElement.GetProperty("result").GetProperty("sequence").GetInt32());
        using var first = JsonDocument.Parse(entries[0]);
        Assert.True(first.RootElement.GetProperty("result").GetProperty("sequence").GetInt32() > 0);
    }

    [Fact]
    public void SwallowsIoFailuresInsteadOfThrowing()
    {
        // Make the .unwritten path unusable by creating it as a FILE.
        Directory.CreateDirectory(_repo);
        File.WriteAllText(Path.Combine(_repo, ".unwritten"), "not a directory");

        FindingsLog.Append(_repo, "cli", new { Failing = false });
    }
}
