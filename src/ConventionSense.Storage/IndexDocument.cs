namespace ConventionSense.Storage;

/// <summary>
/// JSON shape of <c>.conventionsense/index.json</c>. Schema-neutral: entities and
/// transactions, with adapter specifics confined to <see cref="Source"/>.
/// </summary>
public sealed class IndexDocument
{
    public int Version { get; set; } = 1;
    public SourceDocument Source { get; set; } = new();
    public ConfigDocument Config { get; set; } = new();
    public int TransactionCount { get; set; }
    public Dictionary<string, EntityDocument> Entities { get; set; } = [];

    /// <summary>Outer key: ordinally smaller entity id; inner key: the larger one.</summary>
    public Dictionary<string, Dictionary<string, PairDocument>> Pairs { get; set; } = [];

    /// <summary>Labels only for transaction ids referenced by pair examples.</summary>
    public Dictionary<string, TransactionDocument> Transactions { get; set; } = [];

    /// <summary>Facet stats: trigger → companion → facet → counts. Optional (phase 3+).</summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, FacetDocument>>> Facets { get; set; } = [];

    /// <summary>Which companions each trigger has been facet-trained against.</summary>
    public Dictionary<string, List<string>> FacetTraining { get; set; } = [];

    /// <summary>Display locations for location-free entity ids (member index only).</summary>
    public Dictionary<string, string> EntityLocations { get; set; } = [];
}

public sealed class SourceDocument
{
    public string Adapter { get; set; } = "git";
    public string HeadSha { get; set; } = string.Empty;
    public DateTimeOffset IndexedAt { get; set; }
}

public sealed class ConfigDocument
{
    public int MinSupport { get; set; } = 10;
    public int MaxTransactionSize { get; set; } = 30;
    public double DefaultMinConfidence { get; set; } = 0.6;
    public int MaxExamplesPerPair { get; set; } = 10;
    public double FacetFloor { get; set; } = 0.6;
    public int MinFacetSupport { get; set; } = 5;
    public double FacetCandidateFloor { get; set; } = 0.5;
    public int MaxFacetsPerEntity { get; set; } = 64;
    public int FacetMaxDepth { get; set; } = 3;
}

public sealed class EntityDocument
{
    public int Count { get; set; }
}

public sealed class PairDocument
{
    public int Count { get; set; }
    public List<string> Examples { get; set; } = [];
}

public sealed class TransactionDocument
{
    public string Label { get; set; } = string.Empty;
}

public sealed class FacetDocument
{
    public int Count { get; set; }
    public int Co { get; set; }
}
