using System.Globalization;
using Unwritten.Core;
using Unwritten.Git;
using Unwritten.Storage;

namespace Unwritten.Tool;

/// <summary>
/// <c>unwritten check</c> — CLI mode for pre-commit hooks and quick manual checks.
/// Prints holes at or above the report floor; exits 1 if any hole reaches the
/// fail floor. With no files and no --staged, checks everything uncommitted
/// (or everything since --base).
/// </summary>
public static class CheckCommand
{
    private const string Usage = """
        Usage: unwritten check [options] [files]

        With no files and no --staged, checks all uncommitted changes.

        Options:
          --staged                Check the currently staged files.
          --base <rev>            Measure changes against this revision instead of
                                  HEAD (use after committing work in progress).
          --repo <path>           Repository root (default: current directory).
          --min-confidence <x>    Report floor, Wilson lower bound (default 0.6).
          --fail-at <x>           Exit 1 if any hole reaches this confidence (default 0.7).
          --strict                Treat content-suppressed holes as real holes.
        """;

    public static int Run(string[] args, IndexManager indexManager, GitTransactionSource gitSource, TextWriter output, string logSource = "cli")
    {
        bool staged = false;
        bool strict = false;
        string repoPath = Directory.GetCurrentDirectory();
        string baseRevision = "HEAD";
        double? minConfidenceArg = null;
        double? failAtArg = null;
        var files = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--staged":
                    staged = true;
                    break;
                case "--strict":
                    strict = true;
                    break;
                case "--repo" when i + 1 < args.Length:
                    repoPath = args[++i];
                    break;
                case "--base" when i + 1 < args.Length:
                    baseRevision = args[++i];
                    break;
                case "--min-confidence" when i + 1 < args.Length:
                    minConfidenceArg = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--fail-at" when i + 1 < args.Length:
                    failAtArg = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--help" or "-h":
                    output.WriteLine(Usage);
                    return 0;
                case var unknown when unknown.StartsWith('-'):
                    output.WriteLine($"Unknown option: {unknown}");
                    return 2;
                default:
                    files.Add(args[i]);
                    break;
            }
        }

        repoPath = indexManager.ResolveRepoRoot(repoPath);

        // Precedence: command-line flag > .unwritten/config.json > built-in default.
        var config = UnwrittenConfig.Load(repoPath);
        double minConfidence = minConfidenceArg ?? config.DefaultMinConfidence;
        double failAt = failAtArg ?? config.FailConfidence;

        if (minConfidence < 0.3)
        {
            output.WriteLine(Inv($"warning: min-confidence {minConfidence:0.00} is below 0.3 — expect mostly noise (see README validation)."));
        }

        var persisted = indexManager.GetUpToDate(repoPath);

        if (staged)
        {
            files.AddRange(gitSource.GetStagedFiles(repoPath));
            if (files.Count == 0)
            {
                output.WriteLine("Nothing staged — no files to check.");
                return 0;
            }
        }
        else if (files.Count == 0)
        {
            files.AddRange(gitSource.GetChangedFilesSince(repoPath, baseRevision));
            if (files.Count == 0)
            {
                output.WriteLine(baseRevision == "HEAD"
                    ? "Working tree clean — nothing to check."
                    : $"No changes since {baseRevision} — nothing to check.");
                return 0;
            }
        }

        var entities = files.Select(f => EntityPath.Normalize(repoPath, f)).ToArray();
        PrintDataNotes(output, persisted.Index, entities);

        var holes = RuleEngine.FindHoles(persisted.Index, entities, minConfidence);
        var annotated = HoleSuppression.Annotate(persisted.Index, gitSource, repoPath, holes, staged, baseRevision);

        var ignores = IgnoreStore.Load(repoPath);
        annotated = IgnoreFilter.Apply(persisted.Index, annotated, ignores);

        var members = indexManager.GetMembersUpToDate(repoPath);
        var memberReport = MemberHoleFinder.Find(members, gitSource, repoPath, entities, staged, minConfidence, baseRevision);

        var active = annotated.Where(a => !a.Suppressed || strict).ToList();
        var suppressed = annotated.Where(a => a.Suppressed && !strict).ToList();

        IReadOnlyList<HoleResult> memberHoles = memberReport?.Holes ?? [];
        IReadOnlyList<AnnotatedHole> ignoredMemberHoles = [];
        if (!strict && members is not null && memberHoles.Count > 0)
        {
            (memberHoles, ignoredMemberHoles) = IgnoreFilter.SplitMemberHoles(members.Index, memberHoles, ignores);
        }

        var failingFileHoles = active.Where(a => a.Hole.Confidence >= failAt).Select(a => a.Hole).ToList();
        var failingMemberHoles = memberHoles.Where(h => h.Confidence >= failAt).ToList();
        bool failing = failingFileHoles.Count > 0 || failingMemberHoles.Count > 0;

        LogFindings(repoPath, logSource, entities, minConfidence, failAt, active, memberHoles, members,
            suppressed, ignoredMemberHoles, failing);

        if (active.Count == 0 && memberHoles.Count == 0)
        {
            output.WriteLine(Inv($"No holes at confidence >= {minConfidence:0.00} for {entities.Length} file(s)."));
            PrintSuppressed(output, suppressed);
            PrintSuppressed(output, ignoredMemberHoles);
            return 0;
        }

        if (active.Count > 0)
        {
            output.WriteLine($"{active.Count} possible hole(s):");
            output.WriteLine();
            foreach (var annotatedHole in active)
            {
                var hole = annotatedHole.Hole;
                output.WriteLine($"  {hole.Hole}");
                output.WriteLine($"    expected because you changed {hole.Trigger}");
                output.WriteLine(Inv($"    confidence {hole.Confidence:0.000} ({hole.CoChanges} co-changes in {hole.TotalChanges} changes)"));
                foreach (var example in hole.ExampleTransactions)
                {
                    output.WriteLine($"    e.g. {example.Id} {example.Label}");
                }

                output.WriteLine();
            }
        }

        if (memberHoles.Count > 0)
        {
            output.WriteLine($"{memberHoles.Count} member-level hole(s):");
            output.WriteLine();
            foreach (var hole in memberHoles)
            {
                string location = members!.Index.GetEntityLocation(hole.Hole) is { } path ? $" ({path})" : "";
                output.WriteLine($"  {hole.Hole}{location}");
                output.WriteLine($"    expected because you changed {hole.Trigger}");
                output.WriteLine(Inv($"    confidence {hole.Confidence:0.000} ({hole.CoChanges} co-changes in {hole.TotalChanges} changes)"));
                foreach (var example in hole.ExampleTransactions)
                {
                    output.WriteLine($"    e.g. {example.Id} {example.Label}");
                }

                output.WriteLine();
            }
        }

        PrintSuppressed(output, suppressed);
        PrintSuppressed(output, ignoredMemberHoles);

        if (failing)
        {
            output.WriteLine(Inv($"FAIL: at least one hole at confidence >= {failAt:0.00}."));
            PrintDecisionGuide(output, [.. failingFileHoles, .. failingMemberHoles]);
        }

        return failing ? 1 : 0;
    }

    /// <summary>
    /// The exact decision, spelled out per failing hole — the user (possibly via
    /// an agent relaying this verbatim) should never have to construct a command.
    /// </summary>
    private static void PrintDecisionGuide(TextWriter output, IReadOnlyList<HoleResult> failingHoles)
    {
        var holes = failingHoles.DistinctBy(h => (h.Trigger, h.Hole)).ToList();

        output.WriteLine();
        output.WriteLine("Your decision, per hole:");
        output.WriteLine("  1. The warning is right — you forgot this file. Update it and include it in this commit:");
        foreach (var hole in holes)
        {
            output.WriteLine($"       {hole.Hole}   (usually changes together with {hole.Trigger})");
        }

        output.WriteLine("  2. Not needed for THIS commit, but the rule is valid — bypass once");
        output.WriteLine("       (note: skips ALL pre-commit hooks and every hole above at once):");
        output.WriteLine("       git commit --no-verify");
        output.WriteLine("  3. The rule itself is no longer valid — mute it for the next 30 changes of the trigger:");
        foreach (var hole in holes)
        {
            output.WriteLine($"       dotnet tool execute Unwritten --yes -- ignore {hole.Trigger} {hole.Hole} --for 30");
        }

        if (holes.Count > 1)
        {
            output.WriteLine();
            output.WriteLine("  Different decisions for different holes? Fix and/or mute those first,");
            output.WriteLine("  then retry the commit — only if legitimate one-time holes remain, use --no-verify.");
        }
    }

    /// <summary>
    /// Says out loud when a checked file has no or too little history — an empty
    /// result for such a file means "no data", not "no holes".
    /// </summary>
    private static void PrintDataNotes(TextWriter output, CoChangeIndex index, IReadOnlyList<string> entities)
    {
        foreach (var entity in entities)
        {
            int count = index.GetEntityCount(entity);
            if (count == 0)
            {
                output.WriteLine($"note: {entity} has no history in the index (new file, or path mismatch?) — holes cannot be detected for it.");
            }
            else if (count < index.Config.MinSupport)
            {
                output.WriteLine($"note: {entity} has only {count} historical change(s) — rules need {index.Config.MinSupport}.");
            }
        }
    }

    private static void PrintSuppressed(TextWriter output, IReadOnlyList<AnnotatedHole> suppressed)
    {
        foreach (var annotated in suppressed)
        {
            var facets = annotated.Suppression!.ChangedFacets;
            string detail = facets.Count > 0
                ? "changed keys non-predictive: " + string.Join(", ", facets.Select(f => Inv($"{f.Facet} {f.Confidence:0.000}")))
                : annotated.Reason ?? "cosmetic edit";
            output.WriteLine(
                $"suppressed: {annotated.Hole.Hole} (trigger {annotated.Hole.Trigger}; {detail})");
        }
    }

    /// <summary>
    /// Records what this check reported to its consumer in .unwritten/findings.log
    /// (same structured shape as the MCP tool logs, so the log reads uniformly).
    /// </summary>
    private static void LogFindings(
        string repoPath,
        string source,
        IReadOnlyList<string> entities,
        double minConfidence,
        double failAt,
        IReadOnlyList<AnnotatedHole> active,
        IReadOnlyList<HoleResult> memberHoles,
        PersistedIndex? members,
        IReadOnlyList<AnnotatedHole> suppressed,
        IReadOnlyList<AnnotatedHole> ignoredMemberHoles,
        bool failing)
    {
        object HoleDto(HoleResult h, string? holeLocation = null) => new
        {
            h.Hole,
            HoleLocation = holeLocation,
            h.Trigger,
            h.Confidence,
            h.CoChanges,
            h.TotalChanges,
        };

        FindingsLog.Append(repoPath, source, new
        {
            CheckedFiles = entities,
            MinConfidence = minConfidence,
            FailAt = failAt,
            Holes = active.Select(a => HoleDto(a.Hole)).ToArray(),
            MemberHoles = memberHoles.Select(h => HoleDto(h, members!.Index.GetEntityLocation(h.Hole))).ToArray(),
            Suppressed = suppressed.Concat(ignoredMemberHoles)
                .Select(a => new { a.Hole.Hole, a.Hole.Trigger, a.Reason })
                .ToArray(),
            Failing = failing,
        });
    }

    private static string Inv(FormattableString message) => FormattableString.Invariant(message);
}
