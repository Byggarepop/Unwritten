using System.Collections.Concurrent;
using Unwritten.Core;
using Unwritten.Git;

namespace Unwritten.Storage;

/// <summary>
/// Keeps per-repository indexes current: loads from disk, rebuilds or ingests
/// only the new commits when HEAD has moved, and caches in memory so repeated
/// queries within one process are pure lookups. A config.json edit invalidates
/// the cache (tracked via the file's write stamp): floor changes apply on the
/// next query, training-setting changes trigger an automatic full rebuild.
/// </summary>
public sealed class IndexManager(GitTransactionSource source)
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly ConcurrentDictionary<string, string> _rootCache = new(PathComparer);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(PathComparer);
    private readonly ConcurrentDictionary<string, CacheEntry> _memberCache = new(PathComparer);

    private sealed record CacheEntry(PersistedIndex Index, long ConfigStamp);

    /// <summary>
    /// Resolves any path inside a repository to the repository root. Callers
    /// routinely pass a subdirectory (an agent's cwd in a monorepo), which would
    /// otherwise silently produce entity ids that never match the indexed,
    /// root-relative paths. Cached: one git call per distinct path per process.
    /// </summary>
    public string ResolveRepoRoot(string repoPath) =>
        _rootCache.GetOrAdd(Path.GetFullPath(repoPath), source.GetRepositoryRoot);

    /// <summary>
    /// Returns an index whose contents match the repository's current HEAD and
    /// config, building or incrementally updating (and persisting) it if needed.
    /// </summary>
    public PersistedIndex GetUpToDate(string repoPath)
    {
        repoPath = ResolveRepoRoot(repoPath);
        long stamp = ConfigStamp(repoPath);

        // Hot path: HEAD resolved from .git files, config freshness from one file
        // stat — no process spawn. Queries stay pure in-memory lookups as long as
        // neither HEAD nor config.json has moved.
        string? fastHead = GitHead.TryResolve(repoPath);
        if (fastHead is not null &&
            _cache.TryGetValue(repoPath, out var fresh) &&
            fresh.Index.HeadSha == fastHead && fresh.ConfigStamp == stamp)
        {
            return fresh.Index;
        }

        string head = fastHead ?? GetHeadSha(repoPath);
        if (_cache.TryGetValue(repoPath, out var cached) &&
            cached.Index.HeadSha == head && cached.ConfigStamp == stamp)
        {
            return cached.Index;
        }

        var config = UnwrittenConfig.Load(repoPath);
        // Load returns null for corrupt documents and for indexes whose training
        // settings no longer match the config — both fall through to a rebuild.
        var persisted = IndexStore.Load(repoPath, config.ToIndexConfig());
        PersistedIndex current;
        if (persisted is null)
        {
            current = Build(repoPath, head, config);
        }
        else if (persisted.HeadSha == head)
        {
            current = persisted;
        }
        else
        {
            current = TryUpdateIncrementally(repoPath, persisted, head) ?? Build(repoPath, head, config);
        }

        if (persisted is null || persisted.HeadSha != head)
        {
            IndexStore.Save(repoPath, current.Index, current.HeadSha);
        }

        _cache[repoPath] = new CacheEntry(current, stamp);
        return current;
    }

    /// <summary>Discards any existing index and rebuilds from full history (members too, when enabled).</summary>
    public PersistedIndex Rebuild(string repoPath)
    {
        repoPath = ResolveRepoRoot(repoPath);
        long stamp = ConfigStamp(repoPath);
        var config = UnwrittenConfig.Load(repoPath);

        var current = Build(repoPath, GetHeadSha(repoPath), config);
        IndexStore.Save(repoPath, current.Index, current.HeadSha);
        _cache[repoPath] = new CacheEntry(current, stamp);

        if (config.MemberLevel)
        {
            var members = new PersistedIndex(MemberTrainer.Build(source, repoPath, config), current.HeadSha);
            IndexStore.SaveMembers(repoPath, members.Index, members.HeadSha);
            _memberCache[repoPath] = new CacheEntry(members, stamp);
        }

        return current;
    }

    /// <summary>
    /// Member-level index kept current like the file index, or null when member
    /// indexing is not enabled for the repository (config "memberLevel": true).
    /// </summary>
    public PersistedIndex? GetMembersUpToDate(string repoPath)
    {
        repoPath = ResolveRepoRoot(repoPath);
        var config = UnwrittenConfig.Load(repoPath);
        if (!config.MemberLevel)
        {
            return null;
        }

        long stamp = ConfigStamp(repoPath);
        string? fastHead = GitHead.TryResolve(repoPath);
        if (fastHead is not null &&
            _memberCache.TryGetValue(repoPath, out var fresh) &&
            fresh.Index.HeadSha == fastHead && fresh.ConfigStamp == stamp)
        {
            return fresh.Index;
        }

        string head = fastHead ?? GetHeadSha(repoPath);
        var persisted = IndexStore.LoadMembers(repoPath, config.ToMemberIndexConfig());
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

        _memberCache[repoPath] = new CacheEntry(current, stamp);
        return current;
    }

    private string GetHeadSha(string repoPath)
    {
        try
        {
            return source.GetHeadSha(repoPath);
        }
        catch (GitException)
        {
            // rev-parse HEAD after a successful root resolution almost always
            // means an unborn HEAD; the raw git error is cryptic.
            throw new GitException(
                $"'{repoPath}' has no commits yet — Unwritten learns from git history, so it needs at least one commit.");
        }
    }

    private static long ConfigStamp(string repoPath)
    {
        string path = UnwrittenConfig.GetConfigPath(repoPath);
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private PersistedIndex? TryUpdateMembersIncrementally(
        string repoPath, PersistedIndex persisted, string head, UnwrittenConfig config)
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

    private PersistedIndex Build(string repoPath, string head, UnwrittenConfig config)
    {
        var index = new CoChangeIndex(config.ToIndexConfig());
        index.Ingest(source.LoadTransactions(repoPath));
        FacetTrainer.EnsureTrained(index, source, repoPath);
        return new PersistedIndex(index, head);
    }

    private PersistedIndex? TryUpdateIncrementally(string repoPath, PersistedIndex persisted, string head)
    {
        // Mutates the loaded index in place — safe only because `persisted` is a
        // fresh instance from IndexStore.Load, never a cached one another request
        // could be reading concurrently.
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
