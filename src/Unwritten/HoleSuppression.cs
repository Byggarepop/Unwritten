using Unwritten.Core;
using Unwritten.Git;
using Unwritten.Roslyn;
using Unwritten.Storage;

namespace Unwritten.Tool;

/// <summary>A hole plus, when a content layer had something to say, its suppression verdict.</summary>
public sealed record AnnotatedHole(HoleResult Hole, SuppressionResult? Suppression, string? Reason = null)
{
    public bool Suppressed => Suppression?.Suppressed == true;
}

/// <summary>
/// Annotates holes with content-aware suppression:
/// - JSON triggers with facet training: suppress when only non-predictive keys changed (phase 3).
/// - C# triggers: suppress when the current edit changed no member at all — a
///   whitespace/comment-only edit (phase 4).
/// Every failure mode (no data, unparseable, file unreadable, no diff) leaves the
/// hole un-annotated — fail open.
/// </summary>
public static class HoleSuppression
{
    private const string FacetReason = "all changed keys of the trigger are historically non-predictive for this companion";
    private const string CosmeticReason = "the edit changed no member (whitespace/comment-only)";

    public static IReadOnlyList<AnnotatedHole> Annotate(
        CoChangeIndex index,
        GitTransactionSource gitSource,
        string repoPath,
        IReadOnlyList<HoleResult> holes,
        bool staged)
    {
        var jsonFacetsByTrigger = new Dictionary<string, IReadOnlySet<string>?>(StringComparer.Ordinal);
        var csChangedByTrigger = new Dictionary<string, IReadOnlySet<string>?>(StringComparer.Ordinal);
        var annotated = new List<AnnotatedHole>(holes.Count);

        foreach (var hole in holes)
        {
            annotated.Add(AnnotateOne(
                index, gitSource, repoPath, hole, staged, jsonFacetsByTrigger, csChangedByTrigger));
        }

        return annotated;
    }

    private static AnnotatedHole AnnotateOne(
        CoChangeIndex index, GitTransactionSource gitSource, string repoPath, HoleResult hole, bool staged,
        Dictionary<string, IReadOnlySet<string>?> jsonFacetsByTrigger,
        Dictionary<string, IReadOnlySet<string>?> csChangedByTrigger)
    {
        if (FacetTrainer.IsStructured(hole.Trigger) &&
            index.GetFacetStats(hole.Trigger, hole.Hole) is not null)
        {
            if (!jsonFacetsByTrigger.TryGetValue(hole.Trigger, out var facets))
            {
                var contents = ReadBeforeAfter(gitSource, repoPath, hole.Trigger, staged);
                facets = contents is var (before, after)
                    ? JsonFacetDiff.ChangedFacets(before, after, index.Config.FacetMaxDepth, index.Config.MaxFacetsPerEntity)
                    : null;
                jsonFacetsByTrigger[hole.Trigger] = facets;
            }

            if (facets is { Count: > 0 })
            {
                var suppression = RuleEngine.EvaluateSuppression(index, hole.Trigger, hole.Hole, facets);
                return new AnnotatedHole(hole, suppression, suppression?.Suppressed == true ? FacetReason : null);
            }

            return new AnnotatedHole(hole, null);
        }

        if (MemberTrainer.IsEligibleCsFile(hole.Trigger))
        {
            if (!csChangedByTrigger.TryGetValue(hole.Trigger, out var changedMembers))
            {
                var contents = ReadBeforeAfter(gitSource, repoPath, hole.Trigger, staged);
                changedMembers = contents is var (before, after) ? MemberDiff.ChangedMembers(before, after) : null;
                csChangedByTrigger[hole.Trigger] = changedMembers;
            }

            // Suppress only on positive evidence of a cosmetic edit: parsed fine,
            // diffed fine, and not a single member changed.
            if (changedMembers is { Count: 0 })
            {
                return new AnnotatedHole(hole, new SuppressionResult(Suppressed: true, ChangedFacets: []), CosmeticReason);
            }
        }

        return new AnnotatedHole(hole, null);
    }

    /// <summary>Current changed members of an input .cs file (working tree or staged vs HEAD); null = unknown.</summary>
    public static IReadOnlySet<string>? GetChangedMembers(
        GitTransactionSource gitSource, string repoPath, string csFile, bool staged)
    {
        var contents = ReadBeforeAfter(gitSource, repoPath, csFile, staged);
        return contents is var (before, after) ? MemberDiff.ChangedMembers(before, after) : null;
    }

    private static (string Before, string After)? ReadBeforeAfter(
        GitTransactionSource gitSource, string repoPath, string file, bool staged)
    {
        string? before = gitSource.GetFileContentAt(repoPath, "HEAD", file);
        string? after = staged
            ? gitSource.GetStagedFileContent(repoPath, file)
            : ReadWorkingTreeFile(repoPath, file);
        return before is null || after is null ? null : (before, after);
    }

    private static string? ReadWorkingTreeFile(string repoPath, string file)
    {
        try
        {
            string path = Path.Combine(repoPath, file.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
