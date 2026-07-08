using System.Text.Json;
using ConventionSense.Core;

namespace ConventionSense.Storage;

/// <summary>An index together with the source HEAD it was built from.</summary>
public sealed record PersistedIndex(CoChangeIndex Index, string HeadSha);

/// <summary>Loads and saves <c>.conventionsense/index.json</c> in a repository root.</summary>
public static class IndexStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string GetIndexPath(string repoPath) =>
        Path.Combine(repoPath, ".conventionsense", "index.json");

    public static string GetMemberIndexPath(string repoPath) =>
        Path.Combine(repoPath, ".conventionsense", "members.json");

    public static PersistedIndex? Load(string repoPath) => LoadFrom(GetIndexPath(repoPath));

    public static PersistedIndex? LoadMembers(string repoPath) => LoadFrom(GetMemberIndexPath(repoPath));

    private static PersistedIndex? LoadFrom(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var document = JsonSerializer.Deserialize<IndexDocument>(File.ReadAllText(path), JsonOptions);
        if (document is null || document.Version != 1)
        {
            return null;
        }

        var config = new IndexConfig
        {
            MinSupport = document.Config.MinSupport,
            MaxTransactionSize = document.Config.MaxTransactionSize,
            DefaultMinConfidence = document.Config.DefaultMinConfidence,
            MaxExamplesPerPair = document.Config.MaxExamplesPerPair,
            FacetFloor = document.Config.FacetFloor,
            MinFacetSupport = document.Config.MinFacetSupport,
            FacetCandidateFloor = document.Config.FacetCandidateFloor,
            MaxFacetsPerEntity = document.Config.MaxFacetsPerEntity,
            FacetMaxDepth = document.Config.FacetMaxDepth,
        };

        var snapshot = new IndexSnapshot(
            config,
            document.TransactionCount,
            document.Entities.ToDictionary(kv => kv.Key, kv => kv.Value.Count),
            [.. document.Pairs.SelectMany(outer => outer.Value.Select(inner =>
                new PairSnapshot(outer.Key, inner.Key, inner.Value.Count, inner.Value.Examples)))],
            document.Transactions.ToDictionary(kv => kv.Key, kv => kv.Value.Label),
            [.. document.Facets.SelectMany(trigger => trigger.Value.SelectMany(companion =>
                companion.Value.Select(facet => new FacetSnapshot(
                    trigger.Key, companion.Key, facet.Key, facet.Value.Count, facet.Value.Co))))],
            document.FacetTraining.ToDictionary(
                kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value),
            document.EntityLocations);

        return new PersistedIndex(CoChangeIndex.FromSnapshot(snapshot), document.Source.HeadSha);
    }

    public static void Save(string repoPath, CoChangeIndex index, string headSha) =>
        SaveTo(GetIndexPath(repoPath), "git", index, headSha);

    public static void SaveMembers(string repoPath, CoChangeIndex index, string headSha) =>
        SaveTo(GetMemberIndexPath(repoPath), "git+roslyn", index, headSha);

    private static void SaveTo(string path, string adapter, CoChangeIndex index, string headSha)
    {
        var snapshot = index.ToSnapshot();
        var document = new IndexDocument
        {
            Source = new SourceDocument
            {
                Adapter = adapter,
                HeadSha = headSha,
                IndexedAt = DateTimeOffset.UtcNow,
            },
            Config = new ConfigDocument
            {
                MinSupport = snapshot.Config.MinSupport,
                MaxTransactionSize = snapshot.Config.MaxTransactionSize,
                DefaultMinConfidence = snapshot.Config.DefaultMinConfidence,
                MaxExamplesPerPair = snapshot.Config.MaxExamplesPerPair,
                FacetFloor = snapshot.Config.FacetFloor,
                MinFacetSupport = snapshot.Config.MinFacetSupport,
                FacetCandidateFloor = snapshot.Config.FacetCandidateFloor,
                MaxFacetsPerEntity = snapshot.Config.MaxFacetsPerEntity,
                FacetMaxDepth = snapshot.Config.FacetMaxDepth,
            },
            TransactionCount = snapshot.TransactionCount,
            Entities = snapshot.EntityCounts.ToDictionary(
                kv => kv.Key, kv => new EntityDocument { Count = kv.Value }),
            Transactions = snapshot.TransactionLabels.ToDictionary(
                kv => kv.Key, kv => new TransactionDocument { Label = kv.Value }),
        };

        foreach (var pair in snapshot.Pairs)
        {
            if (!document.Pairs.TryGetValue(pair.EntityA, out var inner))
            {
                inner = [];
                document.Pairs[pair.EntityA] = inner;
            }

            inner[pair.EntityB] = new PairDocument { Count = pair.Count, Examples = [.. pair.Examples] };
        }

        foreach (var facet in snapshot.Facets)
        {
            if (!document.Facets.TryGetValue(facet.Trigger, out var byCompanion))
            {
                byCompanion = [];
                document.Facets[facet.Trigger] = byCompanion;
            }

            if (!byCompanion.TryGetValue(facet.Companion, out var byFacet))
            {
                byFacet = [];
                byCompanion[facet.Companion] = byFacet;
            }

            byFacet[facet.Facet] = new FacetDocument { Count = facet.Count, Co = facet.CoChanges };
        }

        document.FacetTraining = snapshot.FacetTraining.ToDictionary(
            kv => kv.Key, kv => kv.Value.ToList());
        document.EntityLocations = new Dictionary<string, string>(snapshot.EntityLocations);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(tempPath, path, overwrite: true);
    }
}
