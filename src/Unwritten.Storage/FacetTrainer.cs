using Unwritten.Core;
using Unwritten.Git;

namespace Unwritten.Storage;

/// <summary>
/// Builds facet-level statistics (which JSON keys of a trigger file predict a
/// companion co-change) for rule participants only. Training walks the trigger
/// file's own history with content diffs — expensive per file, so it runs for
/// the few files that matter, never for all of history.
/// </summary>
public static class FacetTrainer
{
    /// <summary>Structured files that get facet training. JSON only in this phase.</summary>
    public static bool IsStructured(string entity) =>
        entity.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Trigger → companions whose file-level confidence qualifies for facet training.
    /// </summary>
    public static Dictionary<string, HashSet<string>> ComputeCandidates(CoChangeIndex index)
    {
        var candidates = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (entityA, entityB, stats) in index.GetAllPairs())
        {
            foreach (var (trigger, companion) in new[] { (entityA, entityB), (entityB, entityA) })
            {
                if (!IsStructured(trigger))
                {
                    continue;
                }

                int n = index.GetEntityCount(trigger);
                if (n < index.Config.MinSupport ||
                    Wilson.LowerBound(stats.Count, n) < index.Config.FacetCandidateFloor)
                {
                    continue;
                }

                if (!candidates.TryGetValue(trigger, out var companions))
                {
                    companions = new HashSet<string>(StringComparer.Ordinal);
                    candidates[trigger] = companions;
                }

                companions.Add(companion);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Ensures every candidate trigger has facet stats for all its candidate
    /// companions, retraining a trigger from full history when new companions
    /// qualified (the inline-backfill behavior chosen in the design review).
    /// </summary>
    public static void EnsureTrained(CoChangeIndex index, GitTransactionSource source, string repoPath)
    {
        foreach (var (trigger, companions) in ComputeCandidates(index))
        {
            var trained = index.GetFacetTrainedCompanions(trigger);
            if (trained is null || !companions.IsSubsetOf(trained))
            {
                if (trained is not null)
                {
                    companions.UnionWith(trained);
                }

                TrainTrigger(index, source, repoPath, trigger, companions);
            }
        }
    }

    /// <summary>
    /// Extends facet stats of already-trained triggers with freshly ingested
    /// transactions, then backfills any newly qualified pairs.
    /// </summary>
    public static void UpdateIncremental(
        CoChangeIndex index, GitTransactionSource source, string repoPath,
        IReadOnlyList<Transaction> newTransactions)
    {
        foreach (var transaction in newTransactions)
        {
            if (transaction.Entities.Count > index.Config.MaxTransactionSize)
            {
                continue;
            }

            foreach (var trigger in transaction.Entities)
            {
                var companions = index.GetFacetTrainedCompanions(trigger);
                if (companions is null)
                {
                    continue;
                }

                var facets = DiffAtCommit(index, source, repoPath, trigger, transaction.Id);
                if (facets is null || facets.Count == 0)
                {
                    continue;
                }

                foreach (var companion in companions)
                {
                    index.RecordFacetObservation(
                        trigger, companion, facets, transaction.Entities.Contains(companion, StringComparer.Ordinal));
                }
            }
        }

        EnsureTrained(index, source, repoPath);
    }

    private static void TrainTrigger(
        CoChangeIndex index, GitTransactionSource source, string repoPath,
        string trigger, IReadOnlySet<string> companions)
    {
        index.SetFacetTraining(trigger, companions);

        foreach (var commit in source.LoadTransactionsTouching(repoPath, trigger, limit: 0))
        {
            if (commit.Entities.Count > index.Config.MaxTransactionSize)
            {
                continue;
            }

            var facets = DiffAtCommit(index, source, repoPath, trigger, commit.Id);
            if (facets is null || facets.Count == 0)
            {
                continue;
            }

            foreach (var companion in companions)
            {
                index.RecordFacetObservation(
                    trigger, companion, facets, commit.Entities.Contains(companion, StringComparer.Ordinal));
            }
        }
    }

    private static IReadOnlySet<string>? DiffAtCommit(
        CoChangeIndex index, GitTransactionSource source, string repoPath, string trigger, string commitId)
    {
        // No parent version (file added, root commit) or unparseable content →
        // no facet observation for this commit; file-level counts are unaffected.
        string? before = source.GetFileContentAt(repoPath, commitId + "^", trigger);
        string? after = source.GetFileContentAt(repoPath, commitId, trigger);
        if (before is null || after is null)
        {
            return null;
        }

        return JsonFacetDiff.ChangedFacets(
            before, after, index.Config.FacetMaxDepth, index.Config.MaxFacetsPerEntity);
    }
}
