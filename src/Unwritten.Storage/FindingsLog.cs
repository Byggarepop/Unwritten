using System.Text.Json;
using System.Text.Json.Serialization;

namespace Unwritten.Storage;

/// <summary>
/// Append-only log of every findings result the tool hands to a consumer
/// (MCP model, CLI, Stop hook), written to <c>.unwritten/findings.log</c> as a
/// stream of pretty-printed JSON entries. Best-effort: logging must never fail
/// a check, so all I/O errors are swallowed. Size-capped: once the file grows
/// past <see cref="MaxBytes"/> the oldest entries are trimmed away.
/// </summary>
public static class FindingsLog
{
    /// <summary>Cap on the log file size; oldest entries are trimmed once exceeded.</summary>
    private const long MaxBytes = 1024 * 1024;

    /// <summary>Trim down to this size (newest entries kept) so trims stay infrequent.</summary>
    private const long TrimTargetBytes = MaxBytes / 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string GetLogPath(string repoPath) =>
        Path.Combine(repoPath, ".unwritten", "findings.log");

    /// <summary>
    /// Appends one entry recording the findings returned to a consumer.
    /// <paramref name="source"/> names the surface ("mcp", "cli", "stop-hook").
    /// </summary>
    public static void Append(string repoPath, string source, object result)
    {
        try
        {
            string directory = Path.Combine(repoPath, ".unwritten");
            Directory.CreateDirectory(directory);
            IndexStore.EnsureSelfGitignore(directory);

            var entry = new { Timestamp = DateTimeOffset.UtcNow, Source = source, Result = result };
            string path = GetLogPath(repoPath);
            File.AppendAllText(path, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
            TrimIfOverCap(path);
        }
        catch
        {
            // Best-effort by design: a broken log must never fail a check, and on
            // the MCP path stdout is protocol so there is nowhere safe to report.
        }
    }

    private static void TrimIfOverCap(string path)
    {
        if (new FileInfo(path).Length <= MaxBytes)
        {
            return;
        }

        var lines = File.ReadAllLines(path);

        // Entries are pretty-printed, so each spans many lines; an entry starts
        // where its opening brace sits alone at column 0.
        var starts = new List<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i] == "{")
            {
                starts.Add(i);
            }
        }

        if (starts.Count <= 1)
        {
            return;
        }

        // Walk entries newest-first, keeping whole entries (always at least the
        // newest) until the trim target is reached. Char count approximates
        // bytes well enough for a cap on mostly-ASCII JSON.
        long kept = 0;
        int firstKept = -1;
        for (int s = starts.Count - 1; s >= 0; s--)
        {
            int end = s + 1 < starts.Count ? starts[s + 1] : lines.Length;
            long entrySize = 0;
            for (int i = starts[s]; i < end; i++)
            {
                entrySize += lines[i].Length + Environment.NewLine.Length;
            }

            if (firstKept >= 0 && kept + entrySize > TrimTargetBytes)
            {
                break;
            }

            kept += entrySize;
            firstKept = starts[s];
        }

        // Unique temp name then move: an MCP server and a hook can log concurrently.
        string tempPath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetRandomFileName() + ".tmp");
        File.WriteAllLines(tempPath, lines.Skip(firstKept));
        File.Move(tempPath, path, overwrite: true);
    }
}
