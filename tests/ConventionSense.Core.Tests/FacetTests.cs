using ConventionSense.Core;

namespace ConventionSense.Core.Tests;

public class FacetTests
{
    private static CoChangeIndex TrainedIndex()
    {
        var index = new CoChangeIndex();
        index.SetFacetTraining("a.json", ["b.txt"]);

        // "version" changed 12 times, companion co-changed every time (predictive).
        // "description" changed 10 times, companion co-changed 3 times (non-predictive).
        // "rare" changed twice (below MinFacetSupport of 5 → unknown).
        for (int i = 0; i < 12; i++)
        {
            index.RecordFacetObservation("a.json", "b.txt", ["version"], companionChanged: true);
        }

        for (int i = 0; i < 10; i++)
        {
            index.RecordFacetObservation("a.json", "b.txt", ["description"], companionChanged: i < 3);
        }

        index.RecordFacetObservation("a.json", "b.txt", ["rare"], companionChanged: true);
        index.RecordFacetObservation("a.json", "b.txt", ["rare"], companionChanged: true);
        return index;
    }

    [Fact]
    public void ObservationsAreCountedPerFacet()
    {
        var index = TrainedIndex();

        var stats = index.GetFacetStats("a.json", "b.txt")!;
        Assert.Equal(12, stats["version"].Count);
        Assert.Equal(12, stats["version"].CoChanges);
        Assert.Equal(10, stats["description"].Count);
        Assert.Equal(3, stats["description"].CoChanges);
    }

    [Fact]
    public void ObservationsForUntrainedCompanionsAreIgnored()
    {
        var index = TrainedIndex();

        index.RecordFacetObservation("a.json", "other.txt", ["version"], companionChanged: true);
        index.RecordFacetObservation("x.json", "b.txt", ["version"], companionChanged: true);

        Assert.Null(index.GetFacetStats("a.json", "other.txt"));
        Assert.Null(index.GetFacetStats("x.json", "b.txt"));
    }

    [Fact]
    public void TrainedPairWithNoObservationsIsEmptyNotNull()
    {
        var index = new CoChangeIndex();
        index.SetFacetTraining("a.json", ["b.txt"]);

        var stats = index.GetFacetStats("a.json", "b.txt");
        Assert.NotNull(stats);
        Assert.Empty(stats);
    }

    [Fact]
    public void RetrainingClearsOldStats()
    {
        var index = TrainedIndex();

        index.SetFacetTraining("a.json", ["b.txt", "c.txt"]);

        Assert.Empty(index.GetFacetStats("a.json", "b.txt")!);
        Assert.Equal(["b.txt", "c.txt"], index.GetFacetTrainedCompanions("a.json")!.Order());
    }

    [Fact]
    public void FacetsSurviveSnapshotRoundTrip()
    {
        var index = TrainedIndex();

        var restored = CoChangeIndex.FromSnapshot(index.ToSnapshot());

        var stats = restored.GetFacetStats("a.json", "b.txt")!;
        Assert.Equal(12, stats["version"].Count);
        Assert.Equal(3, stats["description"].CoChanges);
        Assert.Equal(["b.txt"], restored.GetFacetTrainedCompanions("a.json")!);
    }

    [Fact]
    public void SuppressesWhenAllChangedFacetsAreNonPredictive()
    {
        var index = TrainedIndex();

        var result = RuleEngine.EvaluateSuppression(index, "a.json", "b.txt", ["description"])!;

        Assert.True(result.Suppressed);
        var assessment = Assert.Single(result.ChangedFacets);
        Assert.Equal(FacetClass.NonPredictive, assessment.Class);
        Assert.InRange(assessment.Confidence, 0.0, 0.59);
    }

    [Fact]
    public void DoesNotSuppressWhenAnyFacetIsPredictive()
    {
        var index = TrainedIndex();

        var result = RuleEngine.EvaluateSuppression(index, "a.json", "b.txt", ["description", "version"])!;

        Assert.False(result.Suppressed);
        Assert.Contains(result.ChangedFacets, f => f.Class == FacetClass.Predictive);
    }

    [Fact]
    public void DoesNotSuppressOnUnknownFacets()
    {
        var index = TrainedIndex();

        // Below MinFacetSupport → unknown, and a never-seen key → unknown. Both fail open.
        Assert.False(RuleEngine.EvaluateSuppression(index, "a.json", "b.txt", ["rare"])!.Suppressed);
        Assert.False(RuleEngine.EvaluateSuppression(index, "a.json", "b.txt", ["brand-new-key"])!.Suppressed);
        Assert.False(RuleEngine.EvaluateSuppression(
            index, "a.json", "b.txt", ["description", "brand-new-key"])!.Suppressed);
    }

    [Fact]
    public void DoesNotSuppressOnEmptyFacetSet()
    {
        var index = TrainedIndex();

        Assert.False(RuleEngine.EvaluateSuppression(index, "a.json", "b.txt", [])!.Suppressed);
    }

    [Fact]
    public void ReturnsNullForUntrainedPairs()
    {
        var index = TrainedIndex();

        Assert.Null(RuleEngine.EvaluateSuppression(index, "a.json", "never-trained.txt", ["version"]));
        Assert.Null(RuleEngine.EvaluateSuppression(index, "untrained.json", "b.txt", ["version"]));
    }
}
