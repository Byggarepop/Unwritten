using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConventionSense.Core;
using ConventionSense.Git;
using ConventionSense.Storage;
using ModelContextProtocol.Server;

namespace ConventionSense.Tool;

/// <summary>MCP tool surface. Each tool ensures the index is current, then queries in memory.</summary>
[McpServerToolType]
public sealed class ConventionSenseTools(IndexManager indexManager, GitTransactionSource gitSource)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerTool(Name = "check_holes")]
    [Description("Given the set of files just changed, flags files that history says are expected to change with them but are absent from the set. Call after editing, passing every file you touched. Each hole comes with evidence: co-change counts and example commits. Cosmetic edits (non-predictive JSON keys, comment-only C# changes) come back with suppressed=true plus the evidence. When member-level indexing is enabled, memberHoles reports absent companion METHODS/members of the members you actually changed.")]
    public string CheckHoles(
        [Description("Repo-relative paths of all files changed in this edit.")] string[] files,
        [Description("Absolute path to the git repository root.")] string repoPath,
        [Description("Wilson lower-bound confidence floor. Omit to use the repo's configured floor (default 0.6, the validated alert threshold).")] double? minConfidence = null)
    {
        var persisted = indexManager.GetUpToDate(repoPath);
        double floor = minConfidence ?? persisted.Index.Config.DefaultMinConfidence;
        var entities = files.Select(f => EntityPath.Normalize(repoPath, f)).ToArray();
        var holes = RuleEngine.FindHoles(persisted.Index, entities, floor);
        var annotated = HoleSuppression.Annotate(persisted.Index, gitSource, repoPath, holes, staged: false);

        var members = indexManager.GetMembersUpToDate(repoPath);
        var memberReport = MemberHoleFinder.Find(members, gitSource, repoPath, entities, staged: false, floor);

        return Serialize(new
        {
            holes = annotated.Select(ToHoleDto),
            memberHoles = memberReport?.Holes.Select(h => new
            {
                h.Hole,
                holeLocation = members!.Index.GetEntityLocation(h.Hole),
                h.Trigger,
                h.Confidence,
                h.CoChanges,
                h.TotalChanges,
                exampleCommits = h.ExampleTransactions.Select(e => new { sha = e.Id, subject = e.Label }),
            }),
            changedMembers = memberReport?.ChangedMembers,
            checkedFiles = entities,
            minConfidence = floor,
        });
    }

    [McpServerTool(Name = "reindex")]
    [Description("Discards the existing index and rebuilds it from the repository's full history. Use after a history rewrite or after changing .conventionsense/config.json training settings.")]
    public string Reindex(
        [Description("Absolute path to the git repository root.")] string repoPath)
    {
        var persisted = indexManager.Rebuild(repoPath);
        return Serialize(StatsReport.Build(repoPath, persisted, indexManager.GetMembersUpToDate(repoPath)));
    }

    [McpServerTool(Name = "stats")]
    [Description("Index health: indexed HEAD, transaction/entity/pair counts, index file size, and rule counts at confidence floors 0.5/0.6/0.7/0.8.")]
    public string Stats(
        [Description("Absolute path to the git repository root.")] string repoPath)
    {
        var persisted = indexManager.GetUpToDate(repoPath);
        return Serialize(StatsReport.Build(repoPath, persisted, indexManager.GetMembersUpToDate(repoPath)));
    }

    [McpServerTool(Name = "expected_companions")]
    [Description("Ranked list of files that historically change together with the given file, regardless of alert threshold. Useful as proactive context when starting to edit a file. Also accepts a member id (e.g. MyNs.MyType.MyMethod/2) when member-level indexing is enabled.")]
    public string ExpectedCompanions(
        [Description("Repo-relative path of the file, or a member id like Namespace.Type.Method/arity.")] string file,
        [Description("Absolute path to the git repository root.")] string repoPath,
        [Description("Maximum number of companions to return.")] int top = 10)
    {
        // Auto-detect member ids by lookup: if the member index knows this entity,
        // answer at member level; otherwise treat the input as a file path.
        var members = indexManager.GetMembersUpToDate(repoPath);
        if (members is not null && members.Index.GetEntityCount(file) > 0)
        {
            var memberCompanions = RuleEngine.GetCompanions(members.Index, file, top);
            return Serialize(new
            {
                member = file,
                location = members.Index.GetEntityLocation(file),
                totalChanges = members.Index.GetEntityCount(file),
                companions = memberCompanions.Select(c => new
                {
                    c.Companion,
                    location = members.Index.GetEntityLocation(c.Companion),
                    c.Confidence,
                    c.CoChanges,
                    c.TotalChanges,
                }),
            });
        }

        var persisted = indexManager.GetUpToDate(repoPath);
        string entity = EntityPath.Normalize(repoPath, file);
        var companions = RuleEngine.GetCompanions(persisted.Index, entity, top);
        return Serialize(new
        {
            file = entity,
            totalChanges = persisted.Index.GetEntityCount(entity),
            companions = companions.Select(c => new
            {
                c.Companion,
                c.Confidence,
                c.CoChanges,
                c.TotalChanges,
            }),
        });
    }

    [McpServerTool(Name = "explain_rule")]
    [Description("Full evidence for the co-change rule between two files: counts, confidence in both directions, historical commits where they changed together, and recent commits where fileA changed alone (the exceptions).")]
    public string ExplainRule(
        [Description("Repo-relative path of the trigger file.")] string fileA,
        [Description("Repo-relative path of the expected companion file.")] string fileB,
        [Description("Absolute path to the git repository root.")] string repoPath)
    {
        // Member-level explanation when both inputs are known member ids.
        var members = indexManager.GetMembersUpToDate(repoPath);
        if (members is not null &&
            members.Index.GetEntityCount(fileA) > 0 && members.Index.GetEntityCount(fileB) > 0)
        {
            var memberExplanation = RuleEngine.ExplainRule(members.Index, fileA, fileB);
            return Serialize(new
            {
                memberExplanation.EntityA,
                locationA = members.Index.GetEntityLocation(fileA),
                memberExplanation.EntityB,
                locationB = members.Index.GetEntityLocation(fileB),
                memberExplanation.TotalChangesA,
                memberExplanation.TotalChangesB,
                memberExplanation.CoChanges,
                memberExplanation.ConfidenceAToB,
                memberExplanation.ConfidenceBToA,
                memberExplanation.RuleAToB,
                memberExplanation.RuleBToA,
                coChangeCommits = memberExplanation.CoChangeExamples.Select(e => new { sha = e.Id, subject = e.Label }),
                note = "member-level rule; 'changed alone' commit listing is file-level only",
            });
        }

        var persisted = indexManager.GetUpToDate(repoPath);
        string entityA = EntityPath.Normalize(repoPath, fileA);
        string entityB = EntityPath.Normalize(repoPath, fileB);
        var explanation = RuleEngine.ExplainRule(persisted.Index, entityA, entityB);

        // The one deliberate git call on the query path: recent commits where A
        // changed without B, so the caller can judge whether "A alone" is normal.
        var aloneCommits = gitSource.LoadTransactionsTouching(repoPath, entityA)
            .Where(t => !t.Entities.Contains(entityB, StringComparer.Ordinal))
            .Take(10)
            .Select(t => new { sha = t.Id, subject = t.Label });

        // Per-key breakdown when A is a facet-trained JSON trigger of B.
        var facetStats = persisted.Index.GetFacetStats(entityA, entityB);
        var facetBreakdown = facetStats?
            .OrderByDescending(kv => kv.Value.Count)
            .Select(kv => new
            {
                facet = kv.Key,
                confidence = Math.Round(Wilson.LowerBound(kv.Value.CoChanges, kv.Value.Count), 4),
                coChanges = kv.Value.CoChanges,
                totalChanges = kv.Value.Count,
                predictive = kv.Value.Count >= persisted.Index.Config.MinFacetSupport &&
                    Wilson.LowerBound(kv.Value.CoChanges, kv.Value.Count) >= persisted.Index.Config.FacetFloor,
            });

        return Serialize(new
        {
            explanation.EntityA,
            explanation.EntityB,
            explanation.TotalChangesA,
            explanation.TotalChangesB,
            explanation.CoChanges,
            explanation.ConfidenceAToB,
            explanation.ConfidenceBToA,
            explanation.RuleAToB,
            explanation.RuleBToA,
            coChangeCommits = explanation.CoChangeExamples.Select(e => new { sha = e.Id, subject = e.Label }),
            recentCommitsWhereAChangedAlone = aloneCommits,
            facetBreakdown,
        });
    }

    private static object ToHoleDto(AnnotatedHole annotated) => new
    {
        annotated.Hole.Hole,
        annotated.Hole.Trigger,
        annotated.Hole.Confidence,
        annotated.Hole.CoChanges,
        annotated.Hole.TotalChanges,
        exampleCommits = annotated.Hole.ExampleTransactions.Select(e => new { sha = e.Id, subject = e.Label }),
        suppressed = annotated.Suppression is null ? (bool?)null : annotated.Suppressed,
        suppressReason = annotated.Reason,
        changedFacets = annotated.Suppression?.ChangedFacets.Select(f => new
        {
            facet = f.Facet,
            @class = f.Class.ToString(),
            f.Confidence,
            f.CoChanges,
            f.TotalChanges,
        }),
    };

    private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);
}
