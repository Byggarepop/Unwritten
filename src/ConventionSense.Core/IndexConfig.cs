namespace ConventionSense.Core;

/// <summary>
/// Tuning knobs for training and scoring. Defaults were validated empirically on
/// EF Core history (see README) — do not change without re-validating.
/// </summary>
public sealed record IndexConfig
{
    /// <summary>Minimum number of changes of the trigger entity before any rule can exist.</summary>
    public int MinSupport { get; init; } = 10;

    /// <summary>Transactions touching more entities than this are excluded from training (refactor/merge noise).</summary>
    public int MaxTransactionSize { get; init; } = 30;

    /// <summary>Default Wilson lower-bound floor for reporting a hole.</summary>
    public double DefaultMinConfidence { get; init; } = 0.6;

    /// <summary>Maximum number of example transaction ids kept per pair as evidence.</summary>
    public int MaxExamplesPerPair { get; init; } = 10;

    /// <summary>Wilson floor for a facet (e.g. JSON key) to count as predictive.</summary>
    public double FacetFloor { get; init; } = 0.6;

    /// <summary>Minimum changes of a facet before it can be classified; below this it is Unknown (no suppression).</summary>
    public int MinFacetSupport { get; init; } = 5;

    /// <summary>File-level confidence from which a structured trigger file gets facet training.</summary>
    public double FacetCandidateFloor { get; init; } = 0.5;

    /// <summary>Beyond this many distinct facets, an entity is facet-untrackable (fail open).</summary>
    public int MaxFacetsPerEntity { get; init; } = 64;

    /// <summary>Facet path depth cap; deeper changes roll up to their ancestor at this depth.</summary>
    public int FacetMaxDepth { get; init; } = 3;
}
