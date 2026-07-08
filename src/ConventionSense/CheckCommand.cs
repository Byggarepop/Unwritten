using System.Globalization;
using ConventionSense.Core;
using ConventionSense.Git;
using ConventionSense.Storage;

namespace ConventionSense.Tool;

/// <summary>
/// <c>conventionsense check</c> — CLI mode for pre-commit hooks. Prints holes at or above
/// the report floor; exits 1 if any hole reaches the fail floor.
/// </summary>
public static class CheckCommand
{
    public static int Run(string[] args, IndexManager indexManager, GitTransactionSource gitSource, TextWriter output)
    {
        bool staged = false;
        bool strict = false;
        string repoPath = Directory.GetCurrentDirectory();
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
                case "--min-confidence" when i + 1 < args.Length:
                    minConfidenceArg = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--fail-at" when i + 1 < args.Length:
                    failAtArg = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case var unknown when unknown.StartsWith('-'):
                    output.WriteLine($"Unknown option: {unknown}");
                    return 2;
                default:
                    files.Add(args[i]);
                    break;
            }
        }

        repoPath = Path.GetFullPath(repoPath);

        // Precedence: command-line flag > .conventionsense/config.json > built-in default.
        var config = ConventionSenseConfig.Load(repoPath);
        double minConfidence = minConfidenceArg ?? config.DefaultMinConfidence;
        double failAt = failAtArg ?? config.FailConfidence;

        if (staged)
        {
            files.AddRange(gitSource.GetStagedFiles(repoPath));
        }

        if (files.Count == 0)
        {
            output.WriteLine(staged
                ? "Nothing staged — no files to check."
                : "No files given. Pass file paths or use --staged.");
            return 0;
        }

        var persisted = indexManager.GetUpToDate(repoPath);
        var entities = files.Select(f => EntityPath.Normalize(repoPath, f)).ToArray();
        var holes = RuleEngine.FindHoles(persisted.Index, entities, minConfidence);
        var annotated = HoleSuppression.Annotate(persisted.Index, gitSource, repoPath, holes, staged);

        var members = indexManager.GetMembersUpToDate(repoPath);
        var memberReport = MemberHoleFinder.Find(members, gitSource, repoPath, entities, staged, minConfidence);

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
