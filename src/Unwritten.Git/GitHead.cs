namespace Unwritten.Git;

/// <summary>
/// Resolves the repository HEAD SHA by reading .git files directly — no process
/// spawn, so up-to-date checks on the query hot path cost microseconds. Returns
/// null whenever anything looks unusual; callers must then fall back to
/// <c>git rev-parse HEAD</c>.
/// </summary>
public static class GitHead
{
    public static string? TryResolve(string repoPath)
    {
        try
        {
            string gitDir = Path.Combine(repoPath, ".git");
            if (File.Exists(gitDir))
            {
                // Worktree or submodule: .git is a file pointing at the real git dir.
                string content = File.ReadAllText(gitDir).Trim();
                if (!content.StartsWith("gitdir:", StringComparison.Ordinal))
                {
                    return null;
                }

                gitDir = content["gitdir:".Length..].Trim();
                if (!Path.IsPathRooted(gitDir))
                {
                    gitDir = Path.GetFullPath(Path.Combine(repoPath, gitDir));
                }
            }

            string headPath = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headPath))
            {
                return null;
            }

            string head = File.ReadAllText(headPath).Trim();
            if (!head.StartsWith("ref:", StringComparison.Ordinal))
            {
                return IsSha(head) ? head : null; // detached HEAD
            }

            string refName = head[4..].Trim();

            // Linked worktrees keep shared refs in the common dir.
            string commonDirFile = Path.Combine(gitDir, "commondir");
            string commonDir = File.Exists(commonDirFile)
                ? Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonDirFile).Trim()))
                : gitDir;

            string refPath = Path.Combine(commonDir, refName.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(refPath))
            {
                string sha = File.ReadAllText(refPath).Trim();
                return IsSha(sha) ? sha : null;
            }

            string packedRefsPath = Path.Combine(commonDir, "packed-refs");
            if (File.Exists(packedRefsPath))
            {
                foreach (string line in File.ReadLines(packedRefsPath))
                {
                    if (line.Length > 41 && line[40] == ' ' &&
                        line.AsSpan(41).Trim().SequenceEqual(refName) && IsSha(line[..40]))
                    {
                        return line[..40];
                    }
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsSha(string value) =>
        value.Length == 40 && value.All(Uri.IsHexDigit);
}
