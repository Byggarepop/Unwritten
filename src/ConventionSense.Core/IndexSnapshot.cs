namespace ConventionSense.Core;

/// <summary>
/// Plain-data projection of a <see cref="CoChangeIndex"/>, used by persistence.
/// Pairs appear once, keyed by the ordinally smaller entity id first.
/// </summary>
public sealed record IndexSnapshot(
    IndexConfig Config,
    int TransactionCount,
    IReadOnlyDictionary<string, int> EntityCounts,
    IReadOnlyList<PairSnapshot> Pairs,
    IReadOnlyDictionary<string, string> TransactionLabels,
    IReadOnlyList<FacetSnapshot> Facets,
    IReadOnlyDictionary<string, IReadOnlyList<string>> FacetTraining,
    IReadOnlyDictionary<string, string> EntityLocations);

public sealed record PairSnapshot(string EntityA, string EntityB, int Count, IReadOnlyList<string> Examples);

public sealed record FacetSnapshot(string Trigger, string Companion, string Facet, int Count, int CoChanges);
