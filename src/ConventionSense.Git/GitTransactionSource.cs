using ConventionSense.Core;

namespace ConventionSense.Git;

/// <summary>
/// Maps git history onto the core's domain: commits become transactions, file
/// paths become entity ids (repo-relative, forward slashes, as-is — no rename
/// following in phase 1).
/// </summary>
public sealed class GitTransactionSource(GitRunner runner)
{
    /// <summary>Length of the abbreviated commit SHA used as transaction id.</summary>
    public const int ShaLength = 12;

    // git renders %x01/%x1f in --pretty=format as these control characters.
    private const char RecordSeparator = (char)0x01;
    private const char FieldSeparator = (char)0x1f;
    private const string PrettyFormat = "--pretty=format:%x01%H%x1f%s";

    public string GetHeadSha(string repoPath) =>
        runner.Run(repoPath, "rev-parse", "HEAD").Trim();

    public bool IsGitRepository(string repoPath)
    {
        try
        {
            return runner.Run(repoPath, "rev-parse", "--is-inside-work-tree").Trim() == "true";
        }
        catch (GitException)
        {
            return false;
        }
    }

    /// <summary>
    /// Loads commits as transactions in chronological order (oldest first), merges
    /// excluded. With <paramref name="sinceSha"/>, only commits after that SHA.
    /// </summary>
    public IReadOnlyList<Transaction> LoadTransactions(string repoPath, string? sinceSha = null)
    {
        string range = sinceSha is null ? "HEAD" : $"{sinceSha}..HEAD";
        string output = runner.Run(
            repoPath,
            "log", range, "--no-merges", "--reverse", "--name-only", PrettyFormat);
        return ParseLog(output);
    }

    /// <summary>
    /// Recent non-merge commits touching <paramref name="path"/>, newest first,
    /// each with its full file list. Used by explain_rule to find the commits
    /// where one entity changed without the other.
    /// </summary>
    public IReadOnlyList<Transaction> LoadTransactionsTouching(string repoPath, string path, int limit = 200)
    {
        // --full-diff: list every file of each selected commit, not just the pathspec —
        // otherwise no companion ever appears and every commit looks like "changed alone".
        var arguments = new List<string>
        {
            "log", "HEAD", "--no-merges", "--name-only", "--full-diff", PrettyFormat,
        };
        if (limit > 0)
        {
            arguments.Insert(arguments.Count - 1, $"--max-count={limit}");
        }

        arguments.Add("--");
        arguments.Add(path);
        return ParseLog(runner.Run(repoPath, [.. arguments]));
    }

    /// <summary>
    /// Content of a file at a revision (e.g. "HEAD", a SHA, "&lt;sha&gt;^"), or null
    /// if the file does not exist there. Use revision ":" prefix form via
    /// <see cref="GetStagedFileContent"/> for the index.
    /// </summary>
    public string? GetFileContentAt(string repoPath, string revision, string path)
    {
        try
        {
            return runner.Run(repoPath, "show", $"{revision}:{path}");
        }
        catch (GitException)
        {
            return null;
        }
    }

    /// <summary>Staged (index) content of a file, or null if not staged/known.</summary>
    public string? GetStagedFileContent(string repoPath, string path)
    {
        try
        {
            return runner.Run(repoPath, "show", $":{path}");
        }
        catch (GitException)
        {
            return null;
        }
    }

    /// <summary>Repo-relative paths of files currently staged.</summary>
    public IReadOnlyList<string> GetStagedFiles(string repoPath) =>
        [.. runner.Run(repoPath, "diff", "--cached", "--name-only")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static List<Transaction> ParseLog(string output)
    {
        var transactions = new List<Transaction>();
        foreach (var record in output.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = record.Split('\n', StringSplitOptions.TrimEntries);
            var header = lines[0].Split(FieldSeparator, 2);
            string id = header[0][..Math.Min(ShaLength, header[0].Length)];
            string subject = header.Length > 1 ? header[1] : string.Empty;
            var files = lines.Skip(1).Where(l => l.Length > 0).ToArray();
            transactions.Add(new Transaction(id, subject, files));
        }

        return transactions;
    }
}
