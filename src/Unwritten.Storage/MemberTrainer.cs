using Unwritten.Core;
using Unwritten.Git;
using Unwritten.Roslyn;

namespace Unwritten.Storage;

/// <summary>
/// Builds the member-level index: for each commit in the history window, diffs
/// every eligible C# file syntactically and ingests the changed members as one
/// transaction into a second <see cref="CoChangeIndex"/>. Blob reads go through
/// one long-lived <c>git cat-file --batch</c> session.
/// </summary>
public static class MemberTrainer
{
    /// <summary>C# source that participates in member training (generated code excluded).</summary>
    public static bool IsEligibleCsFile(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
        !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) &&
        !path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) &&
        !path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase) &&
        !path.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) &&
        !path.Contains("/obj/", StringComparison.OrdinalIgnoreCase);

    public static CoChangeIndex Build(GitTransactionSource source, string repoPath, UnwrittenConfig config)
    {
        var index = new CoChangeIndex(config.ToMemberIndexConfig());
        var transactions = source.LoadTransactions(repoPath);
        var window = transactions.Count > config.MemberHistoryWindow
            ? transactions.Skip(transactions.Count - config.MemberHistoryWindow).ToList()
            : (IReadOnlyList<Transaction>)transactions;

        IngestAll(index, repoPath, window, config);
        return index;
    }

    public static void UpdateIncremental(
        CoChangeIndex index, string repoPath, IReadOnlyList<Transaction> newTransactions, UnwrittenConfig config)
    {
        IngestAll(index, repoPath, newTransactions, config);
    }

    private static void IngestAll(
        CoChangeIndex index, string repoPath, IReadOnlyList<Transaction> transactions, UnwrittenConfig config)
    {
        if (transactions.Count == 0)
        {
            return;
        }

        using var batch = new GitCatFileBatch(repoPath);
        foreach (var transaction in transactions)
        {
            // Same noise philosophy as file level: mega-commits teach nothing.
            if (transaction.Entities.Count == 0 || transaction.Entities.Count > config.MaxTransactionSize)
            {
                continue;
            }

            var changedMembers = new HashSet<string>(StringComparer.Ordinal);
            var locations = new List<(string Member, string File)>();
            foreach (var file in transaction.Entities.Where(IsEligibleCsFile))
            {
                string? before = batch.ReadBlob($"{transaction.Id}^:{file}");
                string? after = batch.ReadBlob($"{transaction.Id}:{file}");
                if (before is null && after is null)
                {
                    continue;
                }

                var changed = MemberDiff.ChangedMembers(before, after);
                if (changed is null)
                {
                    continue; // unparseable at either end — skip this file for this commit
                }

                foreach (var member in changed)
                {
                    changedMembers.Add(member);
                    locations.Add((member, file));
                }
            }

            if (changedMembers.Count == 0 || changedMembers.Count > config.MemberMaxTransactionSize)
            {
                continue;
            }

            index.Ingest(new Transaction(transaction.Id, transaction.Label, changedMembers));
            foreach (var (member, file) in locations)
            {
                index.SetEntityLocation(member, file);
            }
        }
    }
}
