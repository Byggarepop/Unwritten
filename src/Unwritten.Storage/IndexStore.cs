using System.Text.Json;
using Unwritten.Core;

namespace Unwritten.Storage;

/// <summary>An index together with the source HEAD it was built from.</summary>
public sealed record PersistedIndex(CoChangeIndex Index, string HeadSha);

/// <summary>Loads and saves <c>.unwritten/index.json</c> in a repository root.</summary>
public static class IndexStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string GetIndexPath(string repoPath) =>
        Path.Combine(repoPath, ".unwritten", "index.json");

    public static string GetMemberIndexPath(string repoPath) =>
        Path.Combine(repoPath, ".unwritten", "members.json");

    /// <summary>
    /// Loads the persisted index, or null when it is absent, corrupt, a different
    /// schema version, or (when <paramref name="current"/> is given) was trained
    /// with different training settings — null always means "rebuild".
    /// When <paramref name="current"/> is compatible it becomes the loaded index's
    /// config, so query-time floors edited in config.json apply without a rebuild.
    /// </summary>
    public static PersistedIndex? Load(string repoPath, IndexConfig? current = null) =>
        LoadFrom(GetIndexPath(repoPath), current);

    public static PersistedIndex? LoadMembers(string repoPath, IndexConfig? current = null) =>
        LoadFrom(GetMemberIndexPath(repoPath), current);

    private static PersistedIndex? LoadFrom(string path, IndexConfig? current)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        IndexDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<IndexDocument>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            // Corrupt or hand-edited index — treat as absent and let callers rebuild.
            return null;
        }

        if (document is null || document.Version != 1)
        {
            return null;
        }

        if (current is not null && !TrainingCompatible(document.Config, current))
        {
            return null;
        }

        var config = current ?? new IndexConfig
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
            HistoryWindow = document.Config.HistoryWindow,
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

    /// <summary>
    /// True when the settings that shape what gets counted during training match.
    /// Query-time floors (defaultMinConfidence, facetFloor, minFacetSupport) are
    /// deliberately excluded — they apply to an existing index without a rebuild.
    /// </summary>
    private static bool TrainingCompatible(ConfigDocument stored, IndexConfig current) =>
        stored.MinSupport == current.MinSupport &&
        stored.MaxTransactionSize == current.MaxTransactionSize &&
        stored.MaxExamplesPerPair == current.MaxExamplesPerPair &&
        stored.FacetCandidateFloor == current.FacetCandidateFloor &&
        stored.MaxFacetsPerEntity == current.MaxFacetsPerEntity &&
        stored.FacetMaxDepth == current.FacetMaxDepth &&
        stored.HistoryWindow == current.HistoryWindow;

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
                HistoryWindow = snapshot.Config.HistoryWindow,
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

        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        EnsureSelfGitignore(directory);

        // Unique temp name: an MCP server and a pre-commit hook can save concurrently.
        string tempPath = Path.Combine(directory, Path.GetRandomFileName() + ".tmp");
        File.WriteAllText(tempPath, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Makes <c>.unwritten/</c> ignore itself entirely so users never have to edit
    /// their root .gitignore and git status stays clean. Teams that want to share
    /// config.json can still commit it with <c>git add -f</c>.
    /// </summary>
    private static void EnsureSelfGitignore(string directory)
    {
        string gitignorePath = Path.Combine(directory, ".gitignore");
        if (!File.Exists(gitignorePath))
        {
            File.WriteAllText(gitignorePath, "*\n");
        }
    }
}
