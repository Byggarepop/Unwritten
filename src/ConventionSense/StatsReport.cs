using ConventionSense.Core;
using ConventionSense.Storage;

namespace ConventionSense.Tool;

/// <summary>Index health summary shared by the MCP stats tool and the CLI stats command.</summary>
public sealed record MemberStatsReport(
    int TransactionCount,
    int MemberCount,
    int PairCount,
    long IndexFileBytes,
    IReadOnlyDictionary<string, int> RulesAtFloor);

public sealed record StatsReport(
    string IndexedHead,
    int TransactionCount,
    int EntityCount,
    int PairCount,
    int FacetTrainedTriggers,
    long IndexFileBytes,
    IReadOnlyDictionary<string, int> RulesAtFloor,
    MemberStatsReport? MemberIndex,
    IndexConfig Config)
{
    private static readonly double[] Floors = [0.5, 0.6, 0.7, 0.8];

    public static StatsReport Build(string repoPath, PersistedIndex persisted, PersistedIndex? members = null)
    {
        var ruleCounts = RuleEngine.CountRulesAtFloors(persisted.Index, Floors);
        string indexPath = IndexStore.GetIndexPath(Path.GetFullPath(repoPath));

        MemberStatsReport? memberReport = null;
        if (members is not null)
        {
            var memberRuleCounts = RuleEngine.CountRulesAtFloors(members.Index, Floors);
            string memberPath = IndexStore.GetMemberIndexPath(Path.GetFullPath(repoPath));
            memberReport = new MemberStatsReport(
                members.Index.TransactionCount,
                members.Index.EntityCount,
                members.Index.PairCount,
                File.Exists(memberPath) ? new FileInfo(memberPath).Length : 0,
                ToFloorMap(memberRuleCounts));
        }

        return new StatsReport(
            persisted.HeadSha,
            persisted.Index.TransactionCount,
            persisted.Index.EntityCount,
            persisted.Index.PairCount,
            persisted.Index.FacetTrainedTriggers.Count(),
            File.Exists(indexPath) ? new FileInfo(indexPath).Length : 0,
            ToFloorMap(ruleCounts),
            memberReport,
            persisted.Index.Config);
    }

    private static Dictionary<string, int> ToFloorMap(IReadOnlyDictionary<double, int> counts) =>
        Floors.ToDictionary(
            f => f.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), f => counts[f]);
}
