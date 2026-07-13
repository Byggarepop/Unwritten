using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Unwritten.Core;
using Unwritten.Git;
using Unwritten.Storage;
using ModelContextProtocol.Server;

namespace Unwritten.Tool;

/// <summary>MCP tool surface. Each tool ensures the index is current, then queries in memory.</summary>
[McpServerToolType]
public sealed class UnwrittenTools(IndexManager indexManager, GitTransactionSource gitSource)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Explicit floors below this are clamped — validation showed such alerts are ~90% noise.</summary>
    private const double MinUsefulConfidence = 0.3;

    [McpServerTool(Name = "check_holes")]
    [Description("Given the files just changed, flags files that history says are expected to change with them but are absent. Call after editing (or before finishing a task), passing every file you touched — or omit files to auto-detect all uncommitted changes. If you have committed work during this session, pass baseRef (the commit you started from) so committed edits are still seen. Each hole comes with evidence: co-change counts and example commits. checkedFiles reports how much history each input has: for a file with no or too little history an empty result means 'no data', NOT 'all good'. Cosmetic edits (non-predictive JSON keys, comment-only C# changes) come back with suppressed=true plus the evidence. When member-level indexing is enabled, memberHoles reports absent companion METHODS/members of the members you actually changed. If after reviewing the evidence (explain_rule helps) you judge a hole to be a persistently false pattern, DO NOT just dismiss it silently: tell the user and mention that 'unwritten ignore <trigger> <hole> --for <n>' can mute it for a bounded number of trigger changes — only the human can make that call; there is deliberately no MCP tool for it.")]
    public string CheckHoles(
        [Description("Absolute path to the git repository (any path inside it works).")] string repoPath,
        [Description("Repo-relative paths of the files changed in this edit. Omit or leave empty to auto-detect every uncommitted change (or every change since baseRef).")] string[]? files = null,
        [Description("Wilson lower-bound confidence floor. Omit to use the repo's configured floor (default 0.6, the validated alert threshold). Values below 0.3 are clamped — they are noise.")] double? minConfidence = null,
        [Description("Revision the edit is measured against (default HEAD). Pass the pre-work commit SHA when you have already committed during this session.")] string? baseRef = null)
    {
        repoPath = indexManager.ResolveRepoRoot(repoPath);
        string baseRevision = baseRef ?? "HEAD";
        var persisted = indexManager.GetUpToDate(repoPath);
        var notes = new List<string>();

        double floor = minConfidence ?? persisted.Index.Config.DefaultMinConfidence;
        if (minConfidence is not null && floor < MinUsefulConfidence)
        {
            notes.Add($"minConfidence {minConfidence} clamped to {MinUsefulConfidence} — validated: alerts below it are ~90% noise.");
            floor = MinUsefulConfidence;
        }

        string[] inputs = files is { Length: > 0 }
            ? files
            : [.. gitSource.GetChangedFilesSince(repoPath, baseRevision)];
        if (inputs.Length == 0)
        {
            return Serialize(new
            {
                holes = Array.Empty<object>(),
                checkedFiles = Array.Empty<object>(),
                minConfidence = floor,
                notes = new[]
                {
                    baseRef is null
                        ? "No uncommitted changes detected — nothing to check."
                        : $"No changes since {baseRevision} detected — nothing to check.",
                },
            });
        }

        var entities = inputs.Select(f => EntityPath.Normalize(repoPath, f)).ToArray();
        var holes = RuleEngine.FindHoles(persisted.Index, entities, floor);
        var annotated = HoleSuppression.Annotate(persisted.Index, gitSource, repoPath, holes, staged: false, baseRevision);

        var ignores = IgnoreStore.Load(repoPath);
        annotated = IgnoreFilter.Apply(persisted.Index, annotated, ignores);

        var members = indexManager.GetMembersUpToDate(repoPath);
        var memberReport = MemberHoleFinder.Find(members, gitSource, repoPath, entities, staged: false, floor, baseRevision);

        IReadOnlyList<HoleResult> memberHoles = memberReport?.Holes ?? [];
        IReadOnlyList<AnnotatedHole> ignoredMemberHoles = [];
        if (members is not null && memberHoles.Count > 0)
        {
            (memberHoles, ignoredMemberHoles) = IgnoreFilter.SplitMemberHoles(members.Index, memberHoles, ignores);
        }

        int minSupport = persisted.Index.Config.MinSupport;
        var checkedFiles = entities.Select(e =>
        {
            int totalChanges = persisted.Index.GetEntityCount(e);
            return new { file = e, totalChanges, canTriggerRules = totalChanges >= minSupport };
        }).ToArray();

        int unknown = checkedFiles.Count(f => f.totalChanges == 0);
        int thin = checkedFiles.Count(f => f.totalChanges > 0 && !f.canTriggerRules);
        if (unknown > 0)
        {
            notes.Add($"{unknown} checked file(s) have no history in the index (new file, or a path that does not match the repo-relative form?). No holes can be detected for them — an empty result there means 'no data', not 'all good'.");
        }

        if (thin > 0)
        {
            notes.Add($"{thin} checked file(s) have fewer than {minSupport} historical changes — too little history for rules to exist.");
        }

        object MemberDto(HoleResult h, string? suppressReason) => new
        {
            h.Hole,
            holeLocation = members!.Index.GetEntityLocation(h.Hole),
            h.Trigger,
            h.Confidence,
            h.CoChanges,
            h.TotalChanges,
            exampleCommits = h.ExampleTransactions.Select(e => new { sha = e.Id, subject = e.Label }),
            suppressed = suppressReason is null ? (bool?)null : true,
            suppressReason,
        };

        var result = new
        {
            holes = annotated.Select(ToHoleDto).ToArray(),
            memberHoles = memberReport is null
                ? null
                : memberHoles.Select(h => MemberDto(h, null))
                    .Concat(ignoredMemberHoles.Select(a => MemberDto(a.Hole, a.Reason)))
                    .ToArray(),
            changedMembers = memberReport?.ChangedMembers,
            checkedFiles,
            minConfidence = floor,
            baseRef,
            notes = notes.Count > 0 ? notes : null,
        };
        FindingsLog.Append(repoPath, "mcp", result);
        return Serialize(result);
    }

    [McpServerTool(Name = "reindex")]
    [Description("Discards the existing index and rebuilds it from the repository's full history. Use after a history rewrite or after changing .unwritten/config.json training settings.")]
    public string Reindex(
        [Description("Absolute path to the git repository (any path inside it works).")] string repoPath)
    {
        repoPath = indexManager.ResolveRepoRoot(repoPath);
        var persisted = indexManager.Rebuild(repoPath);
        return Serialize(StatsReport.Build(repoPath, persisted, indexManager.GetMembersUpToDate(repoPath)));
    }

    [McpServerTool(Name = "stats")]
    [Description("Index health: indexed HEAD, transaction/entity/pair counts, index file size, and rule counts at confidence floors 0.5/0.6/0.7/0.8.")]
    public string Stats(
        [Description("Absolute path to the git repository (any path inside it works).")] string repoPath)
    {
        repoPath = indexManager.ResolveRepoRoot(repoPath);
        var persisted = indexManager.GetUpToDate(repoPath);
        return Serialize(StatsReport.Build(repoPath, persisted, indexManager.GetMembersUpToDate(repoPath)));
    }

    [McpServerTool(Name = "expected_companions")]
    [Description("Ranked list of files that historically change together with the given file, regardless of alert threshold. Useful as proactive context when starting to edit a file. Also accepts a member id (e.g. MyNs.MyType.MyMethod/2) when member-level indexing is enabled.")]
    public string ExpectedCompanions(
        [Description("Repo-relative path of the file, or a member id like Namespace.Type.Method/arity.")] string file,
        [Description("Absolute path to the git repository (any path inside it works).")] string repoPath,
        [Description("Maximum number of companions to return.")] int top = 10)
    {
        repoPath = indexManager.ResolveRepoRoot(repoPath);

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
        int totalChanges = persisted.Index.GetEntityCount(entity);
        var companions = RuleEngine.GetCompanions(persisted.Index, entity, top);
        return Serialize(new
        {
            file = entity,
            totalChanges,
            companions = companions.Select(c => new
            {
                c.Companion,
                c.Confidence,
                c.CoChanges,
                c.TotalChanges,
            }),
            note = totalChanges == 0
                ? "This file has no history in the index — new file, or a path that does not match the repo-relative form."
                : null,
        });
    }

    [McpServerTool(Name = "explain_rule")]
    [Description("Full evidence for the co-change rule between two files: counts, confidence in both directions, historical commits where they changed together, and recent commits where fileA changed alone (the exceptions). Use this to judge whether a flagged hole is a legitimate exception. If the evidence convinces you the rule is persistently false (e.g. the historical reason for the coupling no longer exists), report that conclusion to the user and mention 'unwritten ignore <trigger> <hole> --for <n>' — muting is the user's decision, not yours.")]
    public string ExplainRule(
        [Description("Repo-relative path of the trigger file.")] string fileA,
        [Description("Repo-relative path of the expected companion file.")] string fileB,
        [Description("Absolute path to the git repository (any path inside it works).")] string repoPath)
    {
        repoPath = indexManager.ResolveRepoRoot(repoPath);

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
        // The exact command to relay when the agent judges the rule false —
        // recommend it to the user verbatim; only the user may run it.
        ifFalsePattern = annotated.Suppressed
            ? null
            : $"recommend to the user (their decision): dotnet tool execute Unwritten --yes -- ignore {annotated.Hole.Trigger} {annotated.Hole.Hole} --for 30",
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
