using ConventionSense.Core;

namespace ConventionSense.Core.Tests;

public class RuleEngineTests
{
    /// <summary>
    /// 12 transactions touching "a"; "b" co-changes in all 12 (wilson_lb(12,12) ≈ 0.76),
    /// "c" in 6 (≈ 0.25). "solo" changes alone 12 times with one co-change with "a".
    /// </summary>
    private static CoChangeIndex BuildIndex()
    {
        var index = new CoChangeIndex();
        for (int i = 1; i <= 12; i++)
        {
            var entities = new List<string> { "a", "b" };
            if (i <= 6)
            {
                entities.Add("c");
            }

            index.Ingest(new Transaction($"t{i}", $"commit {i}", entities));
        }

        for (int i = 1; i <= 12; i++)
        {
            index.Ingest(new Transaction($"s{i}", $"solo {i}", i == 1 ? ["solo", "a"] : ["solo"]));
        }

        return index;
    }

    [Fact]
    public void FindsHoleWhenStrongCompanionIsAbsent()
    {
        var index = BuildIndex();

        var holes = RuleEngine.FindHoles(index, ["a"], minConfidence: 0.6);

        var hole = Assert.Single(holes);
        Assert.Equal("b", hole.Hole);
        Assert.Equal("a", hole.Trigger);
        Assert.Equal(12, hole.CoChanges);
        Assert.Equal(13, hole.TotalChanges); // a also co-changed once with solo
        Assert.InRange(hole.Confidence, 0.6, 1.0);
        Assert.Equal(3, hole.ExampleTransactions.Count);
        Assert.Equal("t12", hole.ExampleTransactions[0].Id); // newest first
        Assert.Equal("commit 12", hole.ExampleTransactions[0].Label);
    }

    [Fact]
    public void NoHoleWhenCompanionIsInTheChangeSet()
    {
        var index = BuildIndex();

        Assert.Empty(RuleEngine.FindHoles(index, ["a", "b"], minConfidence: 0.6));
    }

    [Fact]
    public void WeakCompanionsStayBelowTheFloor()
    {
        var index = BuildIndex();

        var holes = RuleEngine.FindHoles(index, ["a"], minConfidence: 0.6);

        Assert.DoesNotContain(holes, h => h.Hole == "c");
    }

    [Fact]
    public void LowerFloorSurfacesWeakerRules()
    {
        var index = BuildIndex();

        var holes = RuleEngine.FindHoles(index, ["a"], minConfidence: 0.2);

        Assert.Contains(holes, h => h.Hole == "c");
        // Ordered by confidence descending.
        Assert.Equal("b", holes[0].Hole);
    }

    [Fact]
    public void EntitiesBelowMinSupportNeverTrigger()
    {
        var index = new CoChangeIndex();
        for (int i = 1; i <= 5; i++)
        {
            index.Ingest(new Transaction($"t{i}", $"commit {i}", ["x", "y"]));
        }

        // 5 changes < MinSupport of 10, despite 100% co-change.
        Assert.Empty(RuleEngine.FindHoles(index, ["x"], minConfidence: 0.1));
    }

    [Fact]
    public void UnknownEntityYieldsNothing()
    {
        var index = BuildIndex();

        Assert.Empty(RuleEngine.FindHoles(index, ["never-seen"], minConfidence: 0.1));
        Assert.Empty(RuleEngine.GetCompanions(index, "never-seen", top: 5));
    }

    [Fact]
    public void CompanionsAreRankedAndIgnoreTheFloor()
    {
        var index = BuildIndex();

        var companions = RuleEngine.GetCompanions(index, "a", top: 10);

        Assert.Equal(3, companions.Count);
        Assert.Equal("b", companions[0].Companion);
        Assert.Equal("c", companions[1].Companion);
        Assert.Equal("solo", companions[2].Companion);
        Assert.True(companions[0].Confidence > companions[1].Confidence);
    }

    [Fact]
    public void CompanionsRespectsTop()
    {
        var index = BuildIndex();

        Assert.Single(RuleEngine.GetCompanions(index, "a", top: 1));
    }

    [Fact]
    public void ExplainRuleReportsBothDirections()
    {
        var index = BuildIndex();

        var explanation = RuleEngine.ExplainRule(index, "a", "b");

        Assert.Equal(13, explanation.TotalChangesA);
        Assert.Equal(12, explanation.TotalChangesB);
        Assert.Equal(12, explanation.CoChanges);
        // b→a is stronger than a→b: b never changed without a.
        Assert.True(explanation.ConfidenceBToA > explanation.ConfidenceAToB);
        Assert.True(explanation.RuleAToB);
        Assert.True(explanation.RuleBToA);
        Assert.Equal(10, explanation.CoChangeExamples.Count); // capped at MaxExamplesPerPair
    }

    [Fact]
    public void CountsRulesAtEachFloor()
    {
        var index = BuildIndex();

        var counts = RuleEngine.CountRulesAtFloors(index, [0.1, 0.6, 0.99]);

        // At 0.1: a→b, b→a, a→c (0.23), b→c (0.25). c never triggers (6 changes
        // < support); solo→a is 1/12 (~0.015 — below 0.1), a→solo likewise.
        Assert.Equal(4, counts[0.1]);
        // At 0.6: a→b (0.66) and b→a (0.74).
        Assert.Equal(2, counts[0.6]);
        Assert.Equal(0, counts[0.99]);
    }

    [Fact]
    public void PairCountTracksDistinctPairs()
    {
        var index = BuildIndex();

        // {a,b}, {a,c}, {b,c}, {a,solo}
        Assert.Equal(4, index.PairCount);
        Assert.Equal(4, index.GetAllPairs().Count());
        Assert.Equal(
            index.PairCount,
            CoChangeIndex.FromSnapshot(index.ToSnapshot()).PairCount);
    }

    [Fact]
    public void ExplainRuleForUnrelatedPairShowsNoRule()
    {
        var index = BuildIndex();

        var explanation = RuleEngine.ExplainRule(index, "b", "solo");

        Assert.Equal(0, explanation.CoChanges);
        Assert.False(explanation.RuleAToB);
        Assert.False(explanation.RuleBToA);
        Assert.Empty(explanation.CoChangeExamples);
    }
}
