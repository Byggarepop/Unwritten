using System.Globalization;
using ConventionSense.Storage;

namespace ConventionSense.Tool;

/// <summary>
/// <c>conventionsense stats</c> and <c>conventionsense reindex</c> — CLI counterparts of the MCP
/// tools, sharing the same <see cref="StatsReport"/>.
/// </summary>
public static class StatsCommand
{
    public static int Run(string[] args, IndexManager indexManager, TextWriter output, bool rebuild)
    {
        string repoPath = Directory.GetCurrentDirectory();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--repo" when i + 1 < args.Length:
                    repoPath = args[++i];
                    break;
                default:
                    output.WriteLine($"Unknown option: {args[i]}");
                    return 2;
            }
        }

        var persisted = rebuild ? indexManager.Rebuild(repoPath) : indexManager.GetUpToDate(repoPath);
        var report = StatsReport.Build(repoPath, persisted, indexManager.GetMembersUpToDate(repoPath));

        if (rebuild)
        {
            output.WriteLine("Index rebuilt from full history.");
        }

        output.WriteLine($"indexed HEAD:  {report.IndexedHead}");
        output.WriteLine($"transactions:  {report.TransactionCount}");
        output.WriteLine($"entities:      {report.EntityCount}");
        output.WriteLine($"pairs:         {report.PairCount}");
        output.WriteLine($"facet-trained: {report.FacetTrainedTriggers} trigger file(s)");
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"index size:    {report.IndexFileBytes / 1024.0:0.0} KB"));
        foreach (var (floor, count) in report.RulesAtFloor.OrderBy(kv => kv.Key))
        {
            output.WriteLine($"rules >= {floor}: {count}");
        }

        if (report.MemberIndex is { } memberReport)
        {
            output.WriteLine();
            output.WriteLine("member index:");
            output.WriteLine($"  transactions: {memberReport.TransactionCount}");
            output.WriteLine($"  members:      {memberReport.MemberCount}");
            output.WriteLine($"  pairs:        {memberReport.PairCount}");
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  index size:   {memberReport.IndexFileBytes / 1024.0:0.0} KB"));
            foreach (var (floor, count) in memberReport.RulesAtFloor.OrderBy(kv => kv.Key))
            {
                output.WriteLine($"  rules >= {floor}: {count}");
            }
        }

        return 0;
    }
}
