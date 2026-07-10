namespace Unwritten.Core;

/// <summary>
/// In-memory co-change statistics: per-entity change counts and per-pair
/// co-change counts with example evidence. Pure counting — no I/O, no domain
/// assumptions beyond "transactions contain entities".
/// Ingest transactions in chronological order (oldest first) so that example
/// evidence ends up newest-first.
/// </summary>
public sealed class CoChangeIndex
{
    private readonly Dictionary<string, int> _entityCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, PairStats>> _adjacency = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _transactionLabels = new(StringComparer.Ordinal);

    // Facet layer: trigger → companion → facet → counts, plus which companions
    // each trigger has been trained against (so "trained, zero observations"
    // is distinguishable from "never trained").
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, FacetCounts>>> _facets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _facetTraining = new(StringComparer.Ordinal);

    // Optional display annotation: where an entity was last seen (e.g. a member's
    // file path). Entity ids themselves stay location-free.
    private readonly Dictionary<string, string> _entityLocations = new(StringComparer.Ordinal);

    public CoChangeIndex(IndexConfig? config = null)
    {
        Config = config ?? new IndexConfig();
    }

    public IndexConfig Config { get; }

    /// <summary>Number of transactions actually ingested (oversized ones excluded).</summary>
    public int TransactionCount { get; private set; }

    public int EntityCount => _entityCounts.Count;

    /// <summary>Number of distinct unordered pairs with at least one co-change.</summary>
    public int PairCount { get; private set; }

    /// <summary>All unordered pairs, each yielded once (EntityA ordinally smaller).</summary>
    public IEnumerable<(string EntityA, string EntityB, PairStats Stats)> GetAllPairs()
    {
        foreach (var (entityA, neighbors) in _adjacency)
        {
            foreach (var (entityB, stats) in neighbors)
            {
                if (string.CompareOrdinal(entityA, entityB) < 0)
                {
                    yield return (entityA, entityB, stats);
                }
            }
        }
    }

    public void Ingest(IEnumerable<Transaction> transactions)
    {
        foreach (var transaction in transactions)
        {
            Ingest(transaction);
        }
    }

    public void Ingest(Transaction transaction)
    {
        var entities = transaction.Entities
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (entities.Length == 0 || entities.Length > Config.MaxTransactionSize)
        {
            return;
        }

        TransactionCount++;
        foreach (var entity in entities)
        {
            _entityCounts[entity] = _entityCounts.GetValueOrDefault(entity) + 1;
        }

        if (entities.Length > 1)
        {
            _transactionLabels[transaction.Id] = transaction.Label;
        }

        for (int i = 0; i < entities.Length; i++)
        {
            for (int j = i + 1; j < entities.Length; j++)
            {
                var stats = GetOrAddPair(entities[i], entities[j]);
                stats.Count++;
                stats.RecordExample(transaction.Id, Config.MaxExamplesPerPair);
            }
        }
    }

    public int GetEntityCount(string entity) => _entityCounts.GetValueOrDefault(entity);

    public IEnumerable<(string Entity, PairStats Stats)> GetNeighbors(string entity)
    {
        if (_adjacency.TryGetValue(entity, out var neighbors))
        {
            foreach (var (other, stats) in neighbors)
            {
                yield return (other, stats);
            }
        }
    }

    public PairStats? GetPair(string entityA, string entityB) =>
        _adjacency.TryGetValue(entityA, out var neighbors) && neighbors.TryGetValue(entityB, out var stats)
            ? stats
            : null;

    public string? GetTransactionLabel(string transactionId) =>
        _transactionLabels.GetValueOrDefault(transactionId);

    public void SetEntityLocation(string entity, string location) => _entityLocations[entity] = location;

    public string? GetEntityLocation(string entity) => _entityLocations.GetValueOrDefault(entity);

    /// <summary>Marks a trigger as facet-trained against the given companions (replacing earlier training).</summary>
    public void SetFacetTraining(string trigger, IEnumerable<string> companions)
    {
        _facetTraining[trigger] = new HashSet<string>(companions, StringComparer.Ordinal);
        _facets.Remove(trigger);
    }

    /// <summary>Companions the trigger has been facet-trained against, or null if never trained.</summary>
    public IReadOnlySet<string>? GetFacetTrainedCompanions(string trigger) =>
        _facetTraining.GetValueOrDefault(trigger);

    public IEnumerable<string> FacetTrainedTriggers => _facetTraining.Keys;

    /// <summary>
    /// Records one observation: the trigger changed with the given facets changed,
    /// and the companion did or did not co-change. Only counted for companions the
    /// trigger is trained against.
    /// </summary>
    public void RecordFacetObservation(
        string trigger, string companion, IReadOnlyCollection<string> changedFacets, bool companionChanged)
    {
        if (_facetTraining.GetValueOrDefault(trigger)?.Contains(companion) != true)
        {
            return;
        }

        if (!_facets.TryGetValue(trigger, out var byCompanion))
        {
            byCompanion = new Dictionary<string, Dictionary<string, FacetCounts>>(StringComparer.Ordinal);
            _facets[trigger] = byCompanion;
        }

        if (!byCompanion.TryGetValue(companion, out var byFacet))
        {
            byFacet = new Dictionary<string, FacetCounts>(StringComparer.Ordinal);
            byCompanion[companion] = byFacet;
        }

        foreach (var facet in changedFacets)
        {
            if (!byFacet.TryGetValue(facet, out var counts))
            {
                counts = new FacetCounts();
                byFacet[facet] = counts;
            }

            counts.Count++;
            if (companionChanged)
            {
                counts.CoChanges++;
            }
        }
    }

    /// <summary>Per-facet counts for (trigger, companion); null when that pair was never facet-trained.</summary>
    public IReadOnlyDictionary<string, FacetCounts>? GetFacetStats(string trigger, string companion)
    {
        if (_facetTraining.GetValueOrDefault(trigger)?.Contains(companion) != true)
        {
            return null;
        }

        return _facets.GetValueOrDefault(trigger)?.GetValueOrDefault(companion)
            ?? (IReadOnlyDictionary<string, FacetCounts>)new Dictionary<string, FacetCounts>();
    }

    public IndexSnapshot ToSnapshot()
    {
        var pairs = new List<PairSnapshot>();
        var referencedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (entityA, neighbors) in _adjacency)
        {
            foreach (var (entityB, stats) in neighbors)
            {
                // Each pair is stored under both entities; emit it once.
                if (string.CompareOrdinal(entityA, entityB) < 0)
                {
                    pairs.Add(new PairSnapshot(entityA, entityB, stats.Count, stats.Examples));
                    referencedIds.UnionWith(stats.Examples);
                }
            }
        }

        var labels = _transactionLabels
            .Where(kv => referencedIds.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        var facets = new List<FacetSnapshot>();
        foreach (var (trigger, byCompanion) in _facets)
        {
            foreach (var (companion, byFacet) in byCompanion)
            {
                foreach (var (facet, counts) in byFacet)
                {
                    facets.Add(new FacetSnapshot(trigger, companion, facet, counts.Count, counts.CoChanges));
                }
            }
        }

        var training = _facetTraining.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.ToList(), StringComparer.Ordinal);

        return new IndexSnapshot(
            Config, TransactionCount, new Dictionary<string, int>(_entityCounts), pairs, labels, facets, training,
            new Dictionary<string, string>(_entityLocations));
    }

    public static CoChangeIndex FromSnapshot(IndexSnapshot snapshot)
    {
        var index = new CoChangeIndex(snapshot.Config)
        {
            TransactionCount = snapshot.TransactionCount,
        };

        foreach (var (entity, count) in snapshot.EntityCounts)
        {
            index._entityCounts[entity] = count;
        }

        foreach (var pair in snapshot.Pairs)
        {
            var stats = new PairStats(pair.Count, pair.Examples);
            index.GetOrAddNeighborMap(pair.EntityA)[pair.EntityB] = stats;
            index.GetOrAddNeighborMap(pair.EntityB)[pair.EntityA] = stats;
            index.PairCount++;
        }

        foreach (var (id, label) in snapshot.TransactionLabels)
        {
            index._transactionLabels[id] = label;
        }

        foreach (var (trigger, companions) in snapshot.FacetTraining)
        {
            index._facetTraining[trigger] = new HashSet<string>(companions, StringComparer.Ordinal);
        }

        foreach (var facet in snapshot.Facets)
        {
            if (!index._facets.TryGetValue(facet.Trigger, out var byCompanion))
            {
                byCompanion = new Dictionary<string, Dictionary<string, FacetCounts>>(StringComparer.Ordinal);
                index._facets[facet.Trigger] = byCompanion;
            }

            if (!byCompanion.TryGetValue(facet.Companion, out var byFacet))
            {
                byFacet = new Dictionary<string, FacetCounts>(StringComparer.Ordinal);
                byCompanion[facet.Companion] = byFacet;
            }

            byFacet[facet.Facet] = new FacetCounts(facet.Count, facet.CoChanges);
        }

        foreach (var (entity, location) in snapshot.EntityLocations)
        {
            index._entityLocations[entity] = location;
        }

        return index;
    }

    private PairStats GetOrAddPair(string entityA, string entityB)
    {
        var neighborsA = GetOrAddNeighborMap(entityA);
        if (!neighborsA.TryGetValue(entityB, out var stats))
        {
            stats = new PairStats();
            neighborsA[entityB] = stats;
            GetOrAddNeighborMap(entityB)[entityA] = stats;
            PairCount++;
        }

        return stats;
    }

    private Dictionary<string, PairStats> GetOrAddNeighborMap(string entity)
    {
        if (!_adjacency.TryGetValue(entity, out var neighbors))
        {
            neighbors = new Dictionary<string, PairStats>(StringComparer.Ordinal);
            _adjacency[entity] = neighbors;
        }

        return neighbors;
    }
}
