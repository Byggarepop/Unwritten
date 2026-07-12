using System.Text.Json;
using Unwritten.Core;

namespace Unwritten.Storage;

/// <summary>Thrown when <c>.unwritten/config.json</c> is malformed or contains invalid values.</summary>
public sealed class ConfigException(string message) : Exception(message);

/// <summary>
/// Per-repository configuration from <c>.unwritten/config.json</c>. Missing file or
/// missing properties fall back to the validated defaults. Training-related
/// settings (support, mega-commit limit) take effect on the next full rebuild
/// (<c>unwritten reindex</c>); floors take effect immediately.
/// </summary>
public sealed record UnwrittenConfig
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

    /// <summary>
    /// Commented template written on first index build so the settings are
    /// discoverable in place. Every value is commented out on purpose: the file
    /// pins nothing, so improved defaults in future versions still apply.
    /// </summary>
    private const string DefaultTemplate = """
        // Unwritten per-repository configuration.
        // All settings are optional; missing ones use the validated defaults shown.
        // Floors take effect immediately; training settings trigger an automatic
        // rebuild on the next query. See the README for what each setting means.
        {
          // "minSupport": 10,            // min changes of the trigger file before rules exist
          // "maxTransactionSize": 30,    // commits touching more files are excluded from training
          // "defaultMinConfidence": 0.6, // alert floor (check_holes + CLI report floor)
          // "failConfidence": 0.7,       // CLI: exit 1 when a hole reaches this
          // "maxExamplesPerPair": 10,    // evidence commits kept per file pair

          // Content-aware exceptions (JSON trigger files):
          // "facetFloor": 0.6,           // Wilson floor for a key to count as predictive
          // "minFacetSupport": 5,        // min changes of a key before it can be classified
          // "facetCandidateFloor": 0.5,  // file-level confidence from which keys get trained
          // "maxFacetsPerEntity": 64,    // beyond this the file is key-untrackable (fail open)
          // "facetMaxDepth": 3,          // key-path depth cap (deeper rolls up)

          // Method-level indexing (C#/Roslyn), OFF by default:
          // "memberLevel": false,        // enable method->method rules (first build costs seconds-to-minutes)
          // "memberHistoryWindow": 5000, // member training covers this many most-recent commits
          // "memberMinSupport": 10,      // min changes of a trigger method before method rules exist
          // "memberMaxTransactionSize": 50 // commits changing more members are excluded (refactor noise)
        }

        """;

    public static string GetConfigPath(string repoPath) =>
        Path.Combine(repoPath, ".unwritten", "config.json");

    /// <summary>
    /// Writes the commented default template to <c>.unwritten/config.json</c> if
    /// no config exists yet (never overwrites), so users find the settings where
    /// they live instead of in the README.
    /// </summary>
    public static void EnsureDefaultFile(string repoPath)
    {
        string path = GetConfigPath(repoPath);
        if (File.Exists(path))
        {
            return;
        }

        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        IndexStore.EnsureSelfGitignore(directory);
        File.WriteAllText(path, DefaultTemplate);
    }

    public static UnwrittenConfig Load(string repoPath)
    {
        string path = GetConfigPath(repoPath);
        if (!File.Exists(path))
        {
            return new UnwrittenConfig();
        }

        UnwrittenConfig config;
        try
        {
            config = JsonSerializer.Deserialize<UnwrittenConfig>(File.ReadAllText(path), JsonOptions)
                ?? new UnwrittenConfig();
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"{path} is not valid JSON: {ex.Message}");
        }

        config.Validate(path);
        return config;
    }

    private void Validate(string path)
    {
        var problems = new List<string>();
        void Require(bool ok, string problem)
        {
            if (!ok)
            {
                problems.Add(problem);
            }
        }

        Require(MinSupport >= 1, "minSupport must be >= 1");
        Require(MaxTransactionSize >= 1, "maxTransactionSize must be >= 1");
        Require(DefaultMinConfidence is > 0 and <= 1, "defaultMinConfidence must be in (0, 1]");
        Require(FailConfidence is > 0 and <= 1, "failConfidence must be in (0, 1]");
        Require(MaxExamplesPerPair >= 1, "maxExamplesPerPair must be >= 1");
        Require(FacetFloor is > 0 and <= 1, "facetFloor must be in (0, 1]");
        Require(MinFacetSupport >= 1, "minFacetSupport must be >= 1");
        Require(FacetCandidateFloor is > 0 and <= 1, "facetCandidateFloor must be in (0, 1]");
        Require(MaxFacetsPerEntity >= 1, "maxFacetsPerEntity must be >= 1");
        Require(FacetMaxDepth >= 1, "facetMaxDepth must be >= 1");
        Require(MemberHistoryWindow >= 1, "memberHistoryWindow must be >= 1");
        Require(MemberMinSupport >= 1, "memberMinSupport must be >= 1");
        Require(MemberMaxTransactionSize >= 1, "memberMaxTransactionSize must be >= 1");

        if (problems.Count > 0)
        {
            throw new ConfigException($"{path}: {string.Join("; ", problems)}");
        }
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
        HistoryWindow = MemberHistoryWindow,
    };
}
