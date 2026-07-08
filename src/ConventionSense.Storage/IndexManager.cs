using System.Collections.Concurrent;
using ConventionSense.Core;
using ConventionSense.Git;

namespace ConventionSense.Storage;

/// <summary>
/// Keeps per-repository indexes current: loads from disk, rebuilds or ingests
/// only the new commits when HEAD has moved, and caches in memory so repeated
/// queries within one process are pure lookups.
/// </summary>
public sealed class IndexManager(GitTransactionSource source)
{
    private readonly ConcurrentDictionary<string, PersistedIndex> _cache =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, PersistedIndex> _memberCache =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// Returns an index whose contents match the repository's current HEAD,
    /// building or incrementally updating (and persisting) it if needed.
    /// </summary>
    public PersistedIndex GetUpToDate(string repoPath)
    {
        repoPath = Path.GetFullPath(repoPath);

        // Hot path: HEAD resolved from .git files, no process spawn. Queries stay
        // pure in-memory lookups as long as HEAD hasn't moved.
        string? fastHead = GitHead.TryResolve(repoPath);
        if (fastHead is not null &&
            _cache.TryGetValue(repoPath, out var fresh) && fresh.HeadSha == fastHead)
        {
            return fresh;
        }

        if (!source.IsGitRepository(repoPath))
        {
            throw new GitException($"'{repoPath}' is not a git repository.");
        }

        string head = source.GetHeadSha(repoPath);
        if (_cache.TryGetValue(repoPath, out var cached) && cached.HeadSha == head)
        {
            return cached;
        }

        var persisted = IndexStore.Load(repoPath);
        PersistedIndex current;
        if (persisted is null)
        {
            current = Build(repoPath, head);
        }
        else if (persisted.HeadSha == head)
        {
            current = persisted;
        }
        else
        {
            current = TryUpdateIncrementally(repoPath, persisted, head) ?? Build(repoPath, head);
        }

        if (persisted is null || persisted.HeadSha != head)
        {
            IndexStore.Save(repoPath, current.Index, current.HeadSha);
        }

        _cache[repoPath] = current;
        return current;
    }

    /// <summary>Discards any existing index and rebuilds from full history (members too, when enabled).</summary>
    public PersistedIndex Rebuild(string repoPath)
    {
        repoPath = Path.GetFullPath(repoPath);
        if (!source.IsGitRepository(repoPath))
        {
            throw new GitException($"'{repoPath}' is not a git repository.");
        }

        var current = Build(repoPath, source.GetHeadSha(repoPath));
        IndexStore.Save(repoPath, current.Index, current.HeadSha);
        _cache[repoPath] = current;

        var config = ConventionSenseConfig.Load(repoPath);
        if (config.MemberLevel)
        {
            var members = new PersistedIndex(MemberTrainer.Build(source, repoPath, config), current.HeadSha);
            IndexStore.SaveMembers(repoPath, members.Index, members.HeadSha);
            _memberCache[repoPath] = members;
        }

        return current;
    }

    /// <summary>
    /// Member-level index kept current like the file index, or null when member
    /// indexing is not enabled for the repository (config "memberLevel": true).
    /// </summary>
    public PersistedIndex? GetMembersUpToDate(string repoPath)
    {
        repoPath = Path.GetFullPath(repoPath);
        var config = ConventionSenseConfig.Load(repoPath);
        if (!config.MemberLevel)
        {
            return null;
        }

        string? fastHead = GitHead.TryResolve(repoPath);
        if (fastHead is not null &&
            _memberCache.TryGetValue(repoPath, out var fresh) && fresh.HeadSha == fastHead)
        {
            return fresh;
        }

        string head = source.GetHeadSha(repoPath);
        var persisted = IndexStore.LoadMembers(repoPath);
        PersistedIndex current;
        if (persisted is null)
        {
            current = new PersistedIndex(MemberTrainer.Build(source, repoPath, config), head);
        }
        else if (persisted.HeadSha == head)
        {
            current = persisted;
        }
        else
        {
            current = TryUpdateMembersIncrementally(repoPath, persisted, head, config)
                ?? new PersistedIndex(MemberTrainer.Build(source, repoPath, config), head);
        }

        if (persisted is null || persisted.HeadSha != head)
        {
            IndexStore.SaveMembers(repoPath, current.Index, current.HeadSha);
        }

        _memberCache[repoPath] = current;
        return current;
    }

    private PersistedIndex? TryUpdateMembersIncrementally(
        string repoPath, PersistedIndex persisted, string head, ConventionSenseConfig config)
    {
        try
        {
            var newTransactions = source.LoadTransactions(repoPath, sinceSha: persisted.HeadSha);
            MemberTrainer.UpdateIncremental(persisted.Index, repoPath, newTransactions, config);
            return persisted with { HeadSha = head };
        }
        catch (GitException)
        {
            return null;
        }
    }

    private PersistedIndex Build(string repoPath, string head)
    {
        var index = new CoChangeIndex(ConventionSenseConfig.Load(repoPath).ToIndexConfig());
        index.Ingest(source.LoadTransactions(repoPath));
        FacetTrainer.EnsureTrained(index, source, repoPath);
        return new PersistedIndex(index, head);
    }

    private PersistedIndex? TryUpdateIncrementally(string repoPath, PersistedIndex persisted, string head)
    {
        try
        {
            var newTransactions = source.LoadTransactions(repoPath, sinceSha: persisted.HeadSha);
            persisted.Index.Ingest(newTransactions);
            FacetTrainer.UpdateIncremental(persisted.Index, source, repoPath, newTransactions);
            return persisted with { HeadSha = head };
        }
        catch (GitException)
        {
            // Indexed SHA no longer reachable (rebase, force-push, gc) — fall back to a full rebuild.
            return null;
        }
    }
}
