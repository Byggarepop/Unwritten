namespace ConventionSense.Tool;

/// <summary>
/// Normalizes user/agent-supplied file paths to the entity id form used in the
/// index: repo-relative with forward slashes. Accepts absolute paths under the
/// repository and backslash separators.
/// </summary>
public static class EntityPath
{
    public static string Normalize(string repoPath, string file)
    {
        string candidate = file.Replace('\\', '/').TrimStart('/');
        if (Path.IsPathRooted(file))
        {
            string full = Path.GetFullPath(file);
            string root = Path.GetFullPath(repoPath);
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                candidate = Path.GetRelativePath(root, full).Replace('\\', '/');
            }
        }

        return candidate;
    }
}
