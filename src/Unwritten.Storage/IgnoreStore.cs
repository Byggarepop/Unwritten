using System.Text.Json;

namespace Unwritten.Storage;

/// <summary>
/// One muted rule: the directed pair (trigger → hole) stops alerting until the
/// trigger has reached <see cref="UntilTriggerChanges"/> total changes. Bounded
/// by design — permanent ignores rot, and only commits that touch the trigger
/// can re-confirm or erode the rule, so those are what the expiry counts.
/// </summary>
public sealed record IgnoreEntry(
    string Trigger,
    string Hole,
    int UntilTriggerChanges,
    DateTimeOffset CreatedAt,
    string? Note);

/// <summary>
/// Loads and saves <c>.unwritten/ignores.json</c>. Machine-managed (via
/// <c>unwritten ignore</c>) and kept separate from the hand-edited config.json
/// so the tool can rewrite it freely.
/// </summary>
public static class IgnoreStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string GetPath(string repoPath) =>
        Path.Combine(repoPath, ".unwritten", "ignores.json");

    public static IReadOnlyList<IgnoreEntry> Load(string repoPath)
    {
        string path = GetPath(repoPath);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<IgnoreEntry>>(File.ReadAllText(path), JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            // Corrupt mute file: fail toward alerting, never toward silence.
            return [];
        }
    }

    public static void Save(string repoPath, IReadOnlyList<IgnoreEntry> entries)
    {
        string path = GetPath(repoPath);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        IndexStore.EnsureSelfGitignore(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(entries, JsonOptions) + "\n");
    }
}
