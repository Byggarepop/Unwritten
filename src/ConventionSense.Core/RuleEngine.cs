namespace ConventionSense.Core;

/// <summary>Reference to a transaction used as evidence for a rule.</summary>
public sealed record TransactionRef(string Id, string? Label);

/// <summary>An expected entity that is absent from the change set.</summary>
public sealed record HoleResult(
    string Hole,
    string Trigger,
    double Confidence,
    int CoChanges,
    int TotalChanges,
    IReadOnlyList<TransactionRef> ExampleTransactions);

/// <summary>A co-change companion of an entity, ranked by confidence.</summary>
public sealed record CompanionResult(
    string Companion,
    double Confidence,
    int CoChanges,
    int TotalChanges);

/// <summary>Full evidence for the rule between two entities, in both directions.</summary>
public sealed record RuleExplanation(
    string EntityA,
    string EntityB,
    int TotalChangesA,
    int TotalChangesB,
    int CoChanges,
    double ConfidenceAToB,
    double ConfidenceBToA,
    bool RuleAToB,
    bool RuleBToA,
    IReadOnlyList<TransactionRef> CoChangeExamples);

/// <summary>Classification of one changed facet relative to a companion.</summary>
public enum FacetClass
{
    /// <summary>Enough history, confidence at or above the facet floor: changing this facet predicts the companion.</summary>
    Predictive,

    /// <summary>Enough history, confidence below the facet floor.</summary>
    NonPredictive,

    /// <summary>Too little history to classify — never grounds for suppression.</summary>
    Unknown,
}

public sealed record FacetAssessment(
    string Facet, FacetClass Class, double Confidence, int CoChanges, int TotalChanges);

/// <summary>Outcome of evaluating a hole against the facets changed in the current edit.</summary>
public sealed record SuppressionResult(bool Suppressed, IReadOnlyList<FacetAssessment> ChangedFacets);

/// <summary>
/// Read-only queries over a <see cref="CoChangeIndex"/>. Pure in-memory lookups —
/// no I/O, so every query is fast once the index is loaded.
/// </summary>
public static class RuleEngine
{
    /// <summary>
    /// For each input entity A with sufficient support, finds rules A→B where B is
    /// not in the input set. When several inputs point at the same hole, the
    /// highest-confidence trigger is reported. Ordered by confidence descending.
    /// </summary>
    public static IReadOnlyList<HoleResult> FindHoles(
        CoChangeIndex index,
        IReadOnlyCollection<string> entities,
        double minConfidence,
        int exampleCount = 3)
    {
        var inputSet = new HashSet<string>(entities, StringComparer.Ordinal);
        var bestPerHole = new Dictionary<string, HoleResult>(StringComparer.Ordinal);

        foreach (var trigger in inputSet)
        {
            int totalChanges = index.GetEntityCount(trigger);
            if (totalChanges < index.Config.MinSupport)
            {
                continue;
            }

            foreach (var (candidate, stats) in index.GetNeighbors(trigger))
            {
                if (inputSet.Contains(candidate))
                {
                    continue;
                }

                double confidence = Wilson.LowerBound(stats.Count, totalChanges);
                if (confidence < minConfidence)
                {
                    continue;
                }

                if (!bestPerHole.TryGetValue(candidate, out var existing) || confidence > existing.Confidence)
                {
                    bestPerHole[candidate] = new HoleResult(
                        candidate,
                        trigger,
                        Round(confidence),
                        stats.Count,
                        totalChanges,
                        ResolveExamples(index, stats.Examples, exampleCount));
                }
            }
        }

        return [.. bestPerHole.Values.OrderByDescending(h => h.Confidence).ThenBy(h => h.Hole, StringComparer.Ordinal)];
    }

    /// <summary>
    /// All co-change companions of an entity ranked by confidence, regardless of
    /// any alert floor. Empty if the entity has never been seen.
    /// </summary>
    public static IReadOnlyList<CompanionResult> GetCompanions(CoChangeIndex index, string entity, int top)
    {
        int totalChanges = index.GetEntityCount(entity);
        if (totalChanges == 0)
        {
            return [];
        }

        return [.. index.GetNeighbors(entity)
            .Select(n => new CompanionResult(
                n.Entity,
                Round(Wilson.LowerBound(n.Stats.Count, totalChanges)),
                n.Stats.Count,
                totalChanges))
            .OrderByDescending(c => c.Confidence)
            .ThenByDescending(c => c.CoChanges)
            .ThenBy(c => c.Companion, StringComparer.Ordinal)
            .Take(top)];
    }

    /// <summary>Full evidence for the pair (A, B), including both rule directions.</summary>
    public static RuleExplanation ExplainRule(CoChangeIndex index, string entityA, string entityB)
    {
        int totalA = index.GetEntityCount(entityA);
        int totalB = index.GetEntityCount(entityB);
        var stats = index.GetPair(entityA, entityB);
        int coChanges = stats?.Count ?? 0;

        double confidenceAToB = Wilson.LowerBound(coChanges, totalA);
        double confidenceBToA = Wilson.LowerBound(coChanges, totalB);
        double floor = index.Config.DefaultMinConfidence;

        return new RuleExplanation(
            entityA,
            entityB,
            totalA,
            totalB,
            coChanges,
            Round(confidenceAToB),
            Round(confidenceBToA),
            RuleAToB: totalA >= index.Config.MinSupport && confidenceAToB >= floor,
            RuleBToA: totalB >= index.Config.MinSupport && confidenceBToA >= floor,
            stats is null ? [] : ResolveExamples(index, stats.Examples, index.Config.MaxExamplesPerPair));
    }

    /// <summary>
    /// Number of directed rules (trigger has MinSupport changes and the pair's
    /// Wilson lower bound reaches the floor) at each of the given floors.
    /// </summary>
    public static IReadOnlyDictionary<double, int> CountRulesAtFloors(
        CoChangeIndex index, IReadOnlyCollection<double> floors)
    {
        var counts = floors.ToDictionary(f => f, _ => 0);
        foreach (var (entityA, entityB, stats) in index.GetAllPairs())
        {
            foreach (var trigger in new[] { entityA, entityB })
            {
                int n = index.GetEntityCount(trigger);
                if (n < index.Config.MinSupport)
                {
                    continue;
                }

                double confidence = Wilson.LowerBound(stats.Count, n);
                foreach (double floor in floors)
                {
                    if (confidence >= floor)
                    {
                        counts[floor]++;
                    }
                }
            }
        }

        return counts;
    }

    /// <summary>
    /// Evaluates whether the hole (trigger → companion) may be suppressed given the
    /// facets changed in the current edit. Fail open by design: returns null when
    /// the pair has no facet training, and suppresses only when the changed-facet
    /// set is non-empty and every facet is known non-predictive.
    /// </summary>
    public static SuppressionResult? EvaluateSuppression(
        CoChangeIndex index, string trigger, string companion, IReadOnlyCollection<string> changedFacets)
    {
        var stats = index.GetFacetStats(trigger, companion);
        if (stats is null)
        {
            return null;
        }

        var assessments = new List<FacetAssessment>();
        foreach (var facet in changedFacets.Distinct(StringComparer.Ordinal))
        {
            var counts = stats.GetValueOrDefault(facet);
            int n = counts?.Count ?? 0;
            int co = counts?.CoChanges ?? 0;
            double confidence = Wilson.LowerBound(co, n);
            var cls = n < index.Config.MinFacetSupport
                ? FacetClass.Unknown
                : confidence >= index.Config.FacetFloor ? FacetClass.Predictive : FacetClass.NonPredictive;
            assessments.Add(new FacetAssessment(facet, cls, Round(confidence), co, n));
        }

        bool suppressed = assessments.Count > 0 && assessments.All(a => a.Class == FacetClass.NonPredictive);
        return new SuppressionResult(suppressed, assessments);
    }

    private static IReadOnlyList<TransactionRef> ResolveExamples(
        CoChangeIndex index, IReadOnlyList<string> exampleIds, int count) =>
        [.. exampleIds.Take(count).Select(id => new TransactionRef(id, index.GetTransactionLabel(id)))];

    private static double Round(double value) => Math.Round(value, 4);
}
