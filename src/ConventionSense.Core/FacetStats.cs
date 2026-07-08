namespace ConventionSense.Core;

/// <summary>
/// Counts for one facet of a trigger entity relative to one companion:
/// how often the facet changed, and how often the companion co-changed then.
/// </summary>
public sealed class FacetCounts
{
    public FacetCounts()
    {
    }

    internal FacetCounts(int count, int coChanges)
    {
        Count = count;
        CoChanges = coChanges;
    }

    /// <summary>Transactions in which the trigger changed and this facet changed.</summary>
    public int Count { get; internal set; }

    /// <summary>Of those, transactions in which the companion also changed.</summary>
    public int CoChanges { get; internal set; }
}
