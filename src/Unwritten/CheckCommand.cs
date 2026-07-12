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

    public static int Run(string[] args, IndexManager indexManager, GitTransactionSource gitSource, TextWriter output)
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

        var members = indexManager.GetMembersUpToDate(repoPath);
        var memberReport = MemberHoleFinder.Find(members, gitSource, repoPath, entities, staged, minConfidence, baseRevision);

        var active = annotated.Where(a => !a.Suppressed || strict).ToList();
        var suppressed = annotated.Where(a => a.Suppressed && !strict).ToList();
        var memberHoles = memberReport?.Holes ?? [];

        if (active.Count == 0 && memberHoles.Count == 0)
        {
            output.WriteLine(Inv($"No holes at confidence >= {minConfidence:0.00} for {entities.Length} file(s)."));
            PrintSuppressed(output, suppressed);
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
                output.WriteLine(Inv($"    confidence {hole.Confidence:0.00} ({hole.CoChanges} co-changes in {hole.TotalChanges} changes)"));
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
                output.WriteLine(Inv($"    confidence {hole.Confidence:0.00} ({hole.CoChanges} co-changes in {hole.TotalChanges} changes)"));
                foreach (var example in hole.ExampleTransactions)
                {
                    output.WriteLine($"    e.g. {example.Id} {example.Label}");
                }

                output.WriteLine();
            }
        }

        PrintSuppressed(output, suppressed);

        bool failing = active.Any(a => a.Hole.Confidence >= failAt) ||
            memberHoles.Any(h => h.Confidence >= failAt);
        if (failing)
        {
            output.WriteLine(Inv($"FAIL: at least one hole at confidence >= {failAt:0.00}."));
        }

        return failing ? 1 : 0;
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
                ? "changed keys non-predictive: " + string.Join(", ", facets.Select(f => Inv($"{f.Facet} {f.Confidence:0.00}")))
                : annotated.Reason ?? "cosmetic edit";
            output.WriteLine(
                $"suppressed: {annotated.Hole.Hole} (trigger {annotated.Hole.Trigger}; {detail})");
        }
    }

    private static string Inv(FormattableString message) => FormattableString.Invariant(message);
}
