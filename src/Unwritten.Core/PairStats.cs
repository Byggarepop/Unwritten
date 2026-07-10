namespace Unwritten.Core;

/// <summary>
/// Co-change statistics for one unordered entity pair. Shared between both
/// adjacency directions in memory; persisted once under the smaller entity id.
/// </summary>
public sealed class PairStats
{
    private readonly List<string> _examples;

    public PairStats()
    {
        _examples = [];
    }

    internal PairStats(int count, IEnumerable<string> examples)
    {
        Count = count;
        _examples = [.. examples];
    }

    /// <summary>Number of transactions in which both entities appeared.</summary>
    public int Count { get; internal set; }

    /// <summary>Example transaction ids, newest first.</summary>
    public IReadOnlyList<string> Examples => _examples;

    internal void RecordExample(string transactionId, int maxExamples)
    {
        _examples.Insert(0, transactionId);
        if (_examples.Count > maxExamples)
        {
            _examples.RemoveAt(_examples.Count - 1);
        }
    }
}
