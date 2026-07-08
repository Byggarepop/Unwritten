using System.Text.Json;
using ConventionSense.Core;

namespace ConventionSense.Storage;

/// <summary>
/// Per-repository configuration from <c>.conventionsense/config.json</c>. Missing file or
/// missing properties fall back to the validated defaults. Training-related
/// settings (support, mega-commit limit) take effect on the next full rebuild
/// (<c>conventionsense reindex</c>); floors take effect immediately.
/// </summary>
public sealed record ConventionSenseConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    /// <summary>Minimum changes of the trigger file before any rule can exist.</summary>
    public int MinSupport { get; init; } = 10;

    /// <summary>Commits touching more files than this are excluded from training.</summary>
    public int MaxTransactionSize { get; init; } = 30;

    /// <summary>Default alert floor (Wilson lower bound) for reporting a hole.</summary>
    public double DefaultMinConfidence { get; init; } = 0.6;

    /// <summary>CLI: exit 1 when a hole reaches this confidence.</summary>
    public double FailConfidence { get; init; } = 0.7;

    /// <summary>Maximum example commit ids kept per pair as evidence.</summary>
    public int MaxExamplesPerPair { get; init; } = 10;

    /// <summary>Wilson floor for a JSON key to count as predictive of a companion change.</summary>
    public double FacetFloor { get; init; } = 0.6;

    /// <summary>Minimum changes of a key before it can be classified; below → no suppression.</summary>
    public int MinFacetSupport { get; init; } = 5;

    /// <summary>File-level confidence from which a JSON trigger gets key-level training.</summary>
    public double FacetCandidateFloor { get; init; } = 0.5;

    /// <summary>Beyond this many distinct keys, a file is key-untrackable (fail open).</summary>
    public int MaxFacetsPerEntity { get; init; } = 64;

    /// <summary>Key-path depth cap; deeper changes roll up to their ancestor.</summary>
    public int FacetMaxDepth { get; init; } = 3;

    /// <summary>Enable member-level (C#/Roslyn) indexing. Off by default — first build costs minutes on mid-size repos.</summary>
    public bool MemberLevel { get; init; }

    /// <summary>Member training covers this many most-recent commits.</summary>
    public int MemberHistoryWindow { get; init; } = 5000;

    /// <summary>Minimum changes of a trigger member before member rules can exist.</summary>
    public int MemberMinSupport { get; init; } = 10;

    /// <summary>Commits changing more members than this are excluded from member training.</summary>
    public int MemberMaxTransactionSize { get; init; } = 50;

    public static string GetConfigPath(string repoPath) =>
        Path.Combine(repoPath, ".conventionsense", "config.json");

    public static ConventionSenseConfig Load(string repoPath)
    {
        string path = GetConfigPath(repoPath);
        if (!File.Exists(path))
        {
            return new ConventionSenseConfig();
        }

        return JsonSerializer.Deserialize<ConventionSenseConfig>(File.ReadAllText(path), JsonOptions)
            ?? new ConventionSenseConfig();
    }

    public IndexConfig ToIndexConfig() => new()
    {
        MinSupport = MinSupport,
        MaxTransactionSize = MaxTransactionSize,
        DefaultMinConfidence = DefaultMinConfidence,
        MaxExamplesPerPair = MaxExamplesPerPair,
        FacetFloor = FacetFloor,
        MinFacetSupport = MinFacetSupport,
        FacetCandidateFloor = FacetCandidateFloor,
        MaxFacetsPerEntity = MaxFacetsPerEntity,
        FacetMaxDepth = FacetMaxDepth,
    };

    /// <summary>Engine config for the member-level index (facet settings unused there).</summary>
    public IndexConfig ToMemberIndexConfig() => new()
    {
        MinSupport = MemberMinSupport,
        MaxTransactionSize = MemberMaxTransactionSize,
        DefaultMinConfidence = DefaultMinConfidence,
        MaxExamplesPerPair = MaxExamplesPerPair,
    };
}
