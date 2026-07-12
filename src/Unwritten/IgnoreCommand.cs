using System.Globalization;
using Unwritten.Core;
using Unwritten.Storage;

namespace Unwritten.Tool;

/// <summary>
/// Applies active ignore entries to found holes: matched holes become visible
/// suppressions (never silently dropped), consistent with the content-aware
/// layers — <c>--strict</c> overrides them the same way.
/// </summary>
public static class IgnoreFilter
{
    /// <summary>Entries still in effect given the trigger's current change count.</summary>
    public static IReadOnlyList<IgnoreEntry> Active(CoChangeIndex index, IReadOnlyList<IgnoreEntry> entries) =>
        [.. entries.Where(e => index.GetEntityCount(e.Trigger) < e.UntilTriggerChanges)];

    public static IReadOnlyList<AnnotatedHole> Apply(
        CoChangeIndex index, IReadOnlyList<AnnotatedHole> holes, IReadOnlyList<IgnoreEntry> entries)
    {
        var active = Active(index, entries);
        if (active.Count == 0)
        {
            return holes;
        }

        return [.. holes.Select(annotated =>
            FindMatch(active, annotated.Hole) is { } entry && !annotated.Suppressed
                ? new AnnotatedHole(
                    annotated.Hole,
                    new SuppressionResult(Suppressed: true, ChangedFacets: []),
                    Reason(index, entry))
                : annotated)];
    }

    /// <summary>Splits member holes into (kept, ignored) — the member report has no annotation channel.</summary>
    public static (IReadOnlyList<HoleResult> Kept, IReadOnlyList<AnnotatedHole> Ignored) SplitMemberHoles(
        CoChangeIndex memberIndex, IReadOnlyList<HoleResult> holes, IReadOnlyList<IgnoreEntry> entries)
    {
        var active = Active(memberIndex, entries);
        var kept = new List<HoleResult>();
        var ignored = new List<AnnotatedHole>();
        foreach (var hole in holes)
        {
            if (FindMatch(active, hole) is { } entry)
            {
                ignored.Add(new AnnotatedHole(
                    hole, new SuppressionResult(Suppressed: true, ChangedFacets: []), Reason(memberIndex, entry)));
            }
            else
            {
                kept.Add(hole);
            }
        }

        return (kept, ignored);
    }

    private static IgnoreEntry? FindMatch(IReadOnlyList<IgnoreEntry> active, HoleResult hole) =>
        active.FirstOrDefault(e =>
            string.Equals(e.Trigger, hole.Trigger, StringComparison.Ordinal) &&
            string.Equals(e.Hole, hole.Hole, StringComparison.Ordinal));

    private static string Reason(CoChangeIndex index, IgnoreEntry entry)
    {
        int remaining = entry.UntilTriggerChanges - index.GetEntityCount(entry.Trigger);
        return $"ignored via 'unwritten ignore' — expires after {remaining} more change(s) of {entry.Trigger}";
    }
}

/// <summary>
/// <c>unwritten ignore</c> — bounded mute for a false rule. Deliberately CLI-only
/// (no MCP counterpart): muting a warning is a human judgment, not something an
/// agent should do to its own findings.
/// </summary>
public static class IgnoreCommand
{
    private const int DefaultForChanges = 30;

    private const string Usage = """
        Usage:
          unwritten ignore <trigger> <hole> [--for <n>] [--note <text>] [--repo <path>]
          unwritten ignore --list [--repo <path>]
          unwritten ignore --remove <trigger> <hole> [--repo <path>]

        Mutes the rule "changing <trigger> expects <hole>" until <trigger> has
        changed <n> more times (default 30). Bounded on purpose: permanent
        ignores go stale. Muted holes still appear as suppressed (with the
        remaining budget); 'check --strict' overrides them.
        """;

    public static int Run(string[] args, IndexManager indexManager, TextWriter output)
    {
        bool list = false;
        bool remove = false;
        string repoPath = Directory.GetCurrentDirectory();
        int forChanges = DefaultForChanges;
        string? note = null;
        var positional = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--list":
                    list = true;
                    break;
                case "--remove":
                    remove = true;
                    break;
                case "--for" when i + 1 < args.Length:
                    forChanges = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--note" when i + 1 < args.Length:
                    note = args[++i];
                    break;
                case "--repo" when i + 1 < args.Length:
                    repoPath = args[++i];
                    break;
                case "--help" or "-h":
                    output.WriteLine(Usage);
                    return 0;
                case var unknown when unknown.StartsWith('-'):
                    output.WriteLine($"Unknown option: {unknown}");
                    return 2;
                default:
                    positional.Add(args[i]);
                    break;
            }
        }

        repoPath = indexManager.ResolveRepoRoot(repoPath);
        var index = indexManager.GetUpToDate(repoPath).Index;
        var entries = IgnoreStore.Load(repoPath);

        if (list)
        {
            return List(output, index, entries);
        }

        if (positional.Count != 2)
        {
            output.WriteLine(Usage);
            return 2;
        }

        string trigger = EntityPath.Normalize(repoPath, positional[0]);
        string hole = EntityPath.Normalize(repoPath, positional[1]);

        return remove
            ? Remove(output, repoPath, index, entries, trigger, hole)
            : Add(output, repoPath, index, entries, trigger, hole, forChanges, note);
    }

    private static int Add(
        TextWriter output, string repoPath, CoChangeIndex index, IReadOnlyList<IgnoreEntry> entries,
        string trigger, string hole, int forChanges, string? note)
    {
        if (forChanges < 1)
        {
            output.WriteLine("--for must be at least 1.");
            return 2;
        }

        int currentChanges = index.GetEntityCount(trigger);
        if (currentChanges == 0)
        {
            output.WriteLine($"note: {trigger} has no history in the index — check the path; the ignore will match nothing until it does.");
        }

        var pruned = Prune(index, entries);
        var kept = pruned.Where(e => e.Trigger != trigger || e.Hole != hole).ToList();
        var entry = new IgnoreEntry(trigger, hole, currentChanges + forChanges, DateTimeOffset.UtcNow, note);
        kept.Add(entry);
        IgnoreStore.Save(repoPath, kept);

        output.WriteLine($"Ignoring {trigger} -> {hole} for the next {forChanges} change(s) of {trigger}.");
        output.WriteLine("It will show as 'suppressed' with the remaining budget; 'check --strict' overrides it.");
        return 0;
    }

    private static int Remove(
        TextWriter output, string repoPath, CoChangeIndex index, IReadOnlyList<IgnoreEntry> entries,
        string trigger, string hole)
    {
        var kept = Prune(index, entries).Where(e => e.Trigger != trigger || e.Hole != hole).ToList();
        if (kept.Count == entries.Count)
        {
            output.WriteLine($"No ignore found for {trigger} -> {hole}.");
            return 1;
        }

        IgnoreStore.Save(repoPath, kept);
        output.WriteLine($"Removed ignore {trigger} -> {hole}.");
        return 0;
    }

    private static int List(TextWriter output, CoChangeIndex index, IReadOnlyList<IgnoreEntry> entries)
    {
        if (entries.Count == 0)
        {
            output.WriteLine("No ignores.");
            return 0;
        }

        foreach (var entry in entries)
        {
            int remaining = entry.UntilTriggerChanges - index.GetEntityCount(entry.Trigger);
            string status = remaining > 0
                ? $"{remaining} change(s) of the trigger remaining"
                : "expired (inactive; removed on the next ignore command)";
            string noteSuffix = entry.Note is null ? "" : $" — {entry.Note}";
            output.WriteLine($"{entry.Trigger} -> {entry.Hole}: {status}{noteSuffix}");
        }

        return 0;
    }

    /// <summary>Expired entries are dropped whenever the file is rewritten anyway.</summary>
    private static List<IgnoreEntry> Prune(CoChangeIndex index, IReadOnlyList<IgnoreEntry> entries) =>
        [.. entries.Where(e => index.GetEntityCount(e.Trigger) < e.UntilTriggerChanges)];
}
