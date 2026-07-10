using Unwritten.Core;

namespace Unwritten.Core.Tests;

public class CoChangeIndexTests
{
    private static Transaction Tx(string id, params string[] entities) => new(id, $"commit {id}", entities);

    [Fact]
    public void CountsEntitiesAndPairs()
    {
        var index = new CoChangeIndex();
        index.Ingest(Tx("t1", "a", "b"));
        index.Ingest(Tx("t2", "a", "b", "c"));
        index.Ingest(Tx("t3", "a"));

        Assert.Equal(3, index.TransactionCount);
        Assert.Equal(3, index.GetEntityCount("a"));
        Assert.Equal(2, index.GetEntityCount("b"));
        Assert.Equal(1, index.GetEntityCount("c"));
        Assert.Equal(2, index.GetPair("a", "b")!.Count);
        Assert.Equal(1, index.GetPair("a", "c")!.Count);
        Assert.Null(index.GetPair("a", "missing"));
    }

    [Fact]
    public void PairLookupIsSymmetric()
    {
        var index = new CoChangeIndex();
        index.Ingest(Tx("t1", "a", "b"));

        Assert.Same(index.GetPair("a", "b"), index.GetPair("b", "a"));
    }

    [Fact]
    public void DuplicateEntitiesInOneTransactionCountOnce()
    {
        var index = new CoChangeIndex();
        index.Ingest(new Transaction("t1", "dup", ["a", "a", "b"]));

        Assert.Equal(1, index.GetEntityCount("a"));
        Assert.Equal(1, index.GetPair("a", "b")!.Count);
    }

    [Fact]
    public void OversizedTransactionsAreExcluded()
    {
        var index = new CoChangeIndex(new IndexConfig { MaxTransactionSize = 3 });
        index.Ingest(Tx("big", "a", "b", "c", "d"));

        Assert.Equal(0, index.TransactionCount);
        Assert.Equal(0, index.GetEntityCount("a"));
    }

    [Fact]
    public void EmptyTransactionsAreExcluded()
    {
        var index = new CoChangeIndex();
        index.Ingest(Tx("empty"));

        Assert.Equal(0, index.TransactionCount);
    }

    [Fact]
    public void ExamplesAreNewestFirstAndCapped()
    {
        var index = new CoChangeIndex(new IndexConfig { MaxExamplesPerPair = 3 });
        for (int i = 1; i <= 5; i++)
        {
            index.Ingest(Tx($"t{i}", "a", "b"));
        }

        var examples = index.GetPair("a", "b")!.Examples;
        Assert.Equal(["t5", "t4", "t3"], examples);
    }

    [Fact]
    public void SnapshotRoundTripPreservesEverything()
    {
        var index = new CoChangeIndex();
        index.Ingest(Tx("t1", "a", "b"));
        index.Ingest(Tx("t2", "a", "b", "c"));
        index.Ingest(Tx("t3", "b"));

        var restored = CoChangeIndex.FromSnapshot(index.ToSnapshot());

        Assert.Equal(index.TransactionCount, restored.TransactionCount);
        Assert.Equal(index.GetEntityCount("a"), restored.GetEntityCount("a"));
        Assert.Equal(index.GetEntityCount("b"), restored.GetEntityCount("b"));
        Assert.Equal(index.GetPair("a", "b")!.Count, restored.GetPair("a", "b")!.Count);
        Assert.Equal(index.GetPair("a", "b")!.Examples, restored.GetPair("a", "b")!.Examples);
        Assert.Same(restored.GetPair("a", "b"), restored.GetPair("b", "a"));
        Assert.Equal("commit t2", restored.GetTransactionLabel("t2"));
    }

    [Fact]
    public void SnapshotPrunesLabelsOfUnreferencedTransactions()
    {
        var index = new CoChangeIndex(new IndexConfig { MaxExamplesPerPair = 1 });
        index.Ingest(Tx("t1", "a", "b"));
        index.Ingest(Tx("t2", "a", "b"));

        var snapshot = index.ToSnapshot();

        // Only t2 survives as an example, so t1's label must be pruned.
        Assert.Equal(["t2"], snapshot.TransactionLabels.Keys);
    }
}
