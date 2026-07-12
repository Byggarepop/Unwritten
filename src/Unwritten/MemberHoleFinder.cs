using Unwritten.Core;
using Unwritten.Git;
using Unwritten.Storage;

namespace Unwritten.Tool;

public sealed record MemberHoleReport(
    IReadOnlyList<HoleResult> Holes,
    IReadOnlyList<string> ChangedMembers);

/// <summary>
/// Member-level hole detection: determines which members of the input C# files
/// actually changed in the current edit, then queries the member index with that
/// set. Files whose members can't be determined contribute nothing (fail open).
/// </summary>
public static class MemberHoleFinder
{
    public static MemberHoleReport? Find(
        PersistedIndex? members,
        GitTransactionSource gitSource,
        string repoPath,
        IEnumerable<string> entities,
        bool staged,
        double minConfidence,
        string baseRevision = "HEAD")
    {
        if (members is null)
        {
            return null;
        }

        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in entities.Where(MemberTrainer.IsEligibleCsFile))
        {
            var changedInFile = HoleSuppression.GetChangedMembers(gitSource, repoPath, file, staged, baseRevision);
            if (changedInFile is not null)
            {
                changed.UnionWith(changedInFile);
            }
        }

        if (changed.Count == 0)
        {
            return new MemberHoleReport([], []);
        }

        var holes = RuleEngine.FindHoles(members.Index, changed, minConfidence);
        return new MemberHoleReport(holes, [.. changed.Order(StringComparer.Ordinal)]);
    }
}
