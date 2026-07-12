namespace Unwritten.Tool;

/// <summary>
/// Normalizes user/agent-supplied file paths to the entity id form used in the
/// index: repo-relative with forward slashes. Accepts absolute paths under the
/// repository, backslash separators, and leading "./" segments.
/// </summary>
public static class EntityPath
{
    public static string Normalize(string repoPath, string file)
    {
        if (Path.IsPathRooted(file))
        {
            // Path.GetRelativePath applies the platform's case sensitivity and,
            // unlike a raw prefix check, never treats C:\repo2 as inside C:\repo.
            string relative = Path.GetRelativePath(Path.GetFullPath(repoPath), Path.GetFullPath(file));
            if (!Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith(@"..\", StringComparison.Ordinal) &&
                !relative.StartsWith("../", StringComparison.Ordinal))
            {
                return relative.Replace('\\', '/');
            }

            // Outside the repository: pass through unmapped rather than invent
            // a bogus relative id.
            return file.Replace('\\', '/').TrimStart('/');
        }

        string candidate = file.Replace('\\', '/');
        while (candidate.StartsWith("./", StringComparison.Ordinal))
        {
            candidate = candidate[2..];
        }

        return candidate.TrimStart('/');
    }
}
