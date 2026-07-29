using Unwritten.Core;
using Unwritten.Git;

// Replay harness for re-validating MaxTransactionSize (the "30-file cap").
//
// Protocol (mirrors the original EF Core prototype validation, sweeping the cap):
//   1. Load all non-merge commits chronologically.
//   2. Train one index per cap on everything before the holdout window.
//   3. Replay held-out commits oldest-first: for each commit, ask every cap's index
//      for holes, then ingest the commit into every index (incremental, as in real use).
//   4. A hole counts as FILLED if the missing companion changes within the next
//      <lookahead> held-out commits. The final <lookahead> commits are ingested but
//      not evaluated (truncated look-ahead).
//   5. Marginal analysis: holes flagged at cap C that cap 30 did NOT flag on the same
//      commit at the same floor — the rules that only exist because the cap was raised.

var options = EvalOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine(
        "usage: Unwritten.Eval --repo <path> [--holdout 2000] [--lookahead 10] " +
        "[--caps 20,30,50,75,100,200] [--floors 0.6,0.7,0.8] [--max-eval-size 200] [--csv <path>]");
    return 1;
}

var source = new GitTransactionSource(new GitRunner());
Console.WriteLine($"Loading commits from {options.RepoPath} ...");
var all = source.LoadTransactions(options.RepoPath);
Console.WriteLine($"  {all.Count:N0} non-merge commits.");

int holdoutCount = Math.Min(options.Holdout, all.Count / 2);
var training = all.Take(all.Count - holdoutCount).ToArray();
var holdout = all.Skip(all.Count - holdoutCount).ToArray();
Console.WriteLine($"  training: {training.Length:N0}, holdout: {holdout.Length:N0}");

PrintHistogram("training window", training);
PrintHistogram($"holdout (most recent {holdout.Length:N0})", holdout);

// One index per cap, trained on the same stream; only the cap differs.
var indexes = options.Caps.ToDictionary(
    cap => cap,
    cap => new CoChangeIndex(new IndexConfig
    {
        MaxTransactionSize = cap,
        MaxExamplesPerPair = 1, // evidence lists dominate memory and the eval never reads them
    }));

Console.WriteLine("Training ...");
foreach (var (cap, index) in indexes)
{
    index.Ingest(training);
    Console.WriteLine($"  cap {cap,3}: {index.TransactionCount:N0} transactions kept, " +
                      $"{index.EntityCount:N0} entities, {index.PairCount:N0} pairs");
}

// Positions in the holdout stream where each file appears, for O(log n) fill checks.
var appearances = new Dictionary<string, List<int>>(StringComparer.Ordinal);
for (int k = 0; k < holdout.Length; k++)
{
    foreach (var file in holdout[k].Entities.Distinct(StringComparer.Ordinal))
    {
        if (!appearances.TryGetValue(file, out var list))
        {
            appearances[file] = list = [];
        }

        list.Add(k);
    }
}

bool FilledWithin(string file, int afterPosition)
{
    if (!appearances.TryGetValue(file, out var positions))
    {
        return false;
    }

    int i = positions.BinarySearch(afterPosition + 1);
    if (i < 0)
    {
        i = ~i;
    }

    return i < positions.Count && positions[i] <= afterPosition + options.Lookahead;
}

var cells = options.Caps
    .SelectMany(cap => options.Floors.Select(floor => (cap, floor)))
    .ToDictionary(key => key, _ => new CellStats());

int evaluated = 0, skippedLarge = 0;
int lastEvaluable = holdout.Length - options.Lookahead;
double minFloor = options.Floors.Min();

Console.WriteLine("Replaying holdout ...");
for (int k = 0; k < holdout.Length; k++)
{
    var commit = holdout[k];
    bool evaluate = k < lastEvaluable && commit.Entities.Count <= options.MaxEvalSize;
    if (k < lastEvaluable && !evaluate)
    {
        skippedLarge++;
    }

    if (evaluate)
    {
        evaluated++;

        // Cap-30 flags per floor first, so other caps can be compared against them.
        var baseline = options.Floors.ToDictionary(
            floor => floor,
            _ => new HashSet<string>(StringComparer.Ordinal));
        var holesPerCap = new Dictionary<int, IReadOnlyList<HoleResult>>();
        foreach (var (cap, index) in indexes)
        {
            var holes = RuleEngine.FindHoles(index, commit.Entities, minFloor, exampleCount: 0);
            holesPerCap[cap] = holes;
            if (cap == EvalOptions.BaselineCap)
            {
                foreach (var hole in holes)
                {
                    foreach (double floor in options.Floors)
                    {
                        if (hole.Confidence >= floor)
                        {
                            baseline[floor].Add(hole.Hole);
                        }
                    }
                }
            }
        }

        foreach (var (cap, holes) in holesPerCap)
        {
            foreach (var hole in holes)
            {
                bool filled = FilledWithin(hole.Hole, k);
                foreach (double floor in options.Floors)
                {
                    if (hole.Confidence < floor)
                    {
                        continue;
                    }

                    var cell = cells[(cap, floor)];
                    cell.Holes++;
                    if (filled)
                    {
                        cell.Filled++;
                    }

                    if (cap != EvalOptions.BaselineCap && !baseline[floor].Contains(hole.Hole))
                    {
                        cell.MarginalHoles++;
                        if (filled)
                        {
                            cell.MarginalFilled++;
                        }
                    }
                }
            }
        }
    }

    foreach (var index in indexes.Values)
    {
        index.Ingest(commit);
    }

    if ((k + 1) % 500 == 0)
    {
        Console.WriteLine($"  {k + 1:N0}/{holdout.Length:N0} commits replayed");
    }
}

Console.WriteLine($"Evaluated {evaluated:N0} commits " +
                  $"({skippedLarge:N0} skipped as >{options.MaxEvalSize} files, " +
                  $"{options.Lookahead} tail commits ingest-only).");

var ruleCounts = indexes.ToDictionary(
    pair => pair.Key,
    pair => RuleEngine.CountRulesAtFloors(pair.Value, options.Floors));

string repoName = Path.GetFileNameWithoutExtension(
    options.RepoPath.TrimEnd('/', '\\'));

Console.WriteLine();
Console.WriteLine($"| cap | floor | rules | holes | filled | fill% | marginal | m.filled | m.fill% |");
Console.WriteLine($"|----:|------:|------:|------:|-------:|------:|---------:|---------:|--------:|");
var csvLines = new List<string>
{
    "repo,cap,floor,rules,holes,filled,fillRate,marginalHoles,marginalFilled,marginalFillRate",
};
foreach (int cap in options.Caps)
{
    foreach (double floor in options.Floors)
    {
        var cell = cells[(cap, floor)];
        int rules = ruleCounts[cap][floor];
        string mark = cap == EvalOptions.BaselineCap ? "*" : " ";
        Console.WriteLine(
            $"| {cap,3}{mark}| {floor:0.0}   | {rules,5:N0} | {cell.Holes,5:N0} | {cell.Filled,6:N0} " +
            $"| {Percent(cell.Filled, cell.Holes),5} | {cell.MarginalHoles,8:N0} | {cell.MarginalFilled,8:N0} " +
            $"| {Percent(cell.MarginalFilled, cell.MarginalHoles),7} |");
        csvLines.Add(string.Join(',',
            repoName, cap, floor, rules, cell.Holes, cell.Filled, Rate(cell.Filled, cell.Holes),
            cell.MarginalHoles, cell.MarginalFilled, Rate(cell.MarginalFilled, cell.MarginalHoles)));
    }
}

Console.WriteLine($"(* = baseline cap {EvalOptions.BaselineCap}; marginal = holes cap 30 did not flag on the same commit)");

if (options.CsvPath is not null)
{
    File.WriteAllLines(options.CsvPath, csvLines);
    Console.WriteLine($"CSV written to {options.CsvPath}");
}

return 0;

static string Percent(int part, int whole) =>
    whole == 0 ? "-" : $"{100.0 * part / whole:0.0}%";

static string Rate(int part, int whole) =>
    whole == 0 ? "" : (1.0 * part / whole).ToString("0.000");

static void PrintHistogram(string title, IReadOnlyList<Transaction> transactions)
{
    (int Min, int Max)[] buckets = [(1, 5), (6, 10), (11, 20), (21, 30), (31, 50), (51, 75), (76, 100), (101, 200), (201, int.MaxValue)];
    var counts = new int[buckets.Length];
    foreach (var transaction in transactions)
    {
        int size = transaction.Entities.Count;
        for (int i = 0; i < buckets.Length; i++)
        {
            if (size >= buckets[i].Min && size <= buckets[i].Max)
            {
                counts[i]++;
                break;
            }
        }
    }

    int total = transactions.Count;
    int above30 = transactions.Count(t => t.Entities.Count > 30);
    Console.WriteLine($"Commit-size histogram — {title}:");
    for (int i = 0; i < buckets.Length; i++)
    {
        string label = buckets[i].Max == int.MaxValue ? $">{buckets[i].Min - 1}" : $"{buckets[i].Min}-{buckets[i].Max}";
        Console.WriteLine($"  {label,8} files: {counts[i],7:N0}  ({Percent(counts[i], total)})");
    }

    Console.WriteLine($"  excluded by cap 30: {above30:N0} of {total:N0} ({Percent(above30, total)})");
}

internal sealed class CellStats
{
    public int Holes;
    public int Filled;
    public int MarginalHoles;
    public int MarginalFilled;
}

internal sealed record EvalOptions
{
    public const int BaselineCap = 30;

    public required string RepoPath { get; init; }
    public int Holdout { get; init; } = 2000;
    public int Lookahead { get; init; } = 10;
    public int MaxEvalSize { get; init; } = 200;
    public IReadOnlyList<int> Caps { get; init; } = [20, 30, 50, 75, 100, 200];
    public IReadOnlyList<double> Floors { get; init; } = [0.6, 0.7, 0.8];
    public string? CsvPath { get; init; }

    public static EvalOptions? Parse(string[] args)
    {
        string? repo = null, csv = null;
        int holdout = 2000, lookahead = 10, maxEvalSize = 200;
        int[] caps = [20, 30, 50, 75, 100, 200];
        double[] floors = [0.6, 0.7, 0.8];
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--repo": repo = args[i + 1]; break;
                case "--holdout": holdout = int.Parse(args[i + 1]); break;
                case "--lookahead": lookahead = int.Parse(args[i + 1]); break;
                case "--max-eval-size": maxEvalSize = int.Parse(args[i + 1]); break;
                case "--caps": caps = [.. args[i + 1].Split(',').Select(int.Parse)]; break;
                case "--floors": floors = [.. args[i + 1].Split(',').Select(double.Parse)]; break;
                case "--csv": csv = args[i + 1]; break;
                default: return null;
            }
        }

        if (repo is null || !caps.Contains(BaselineCap))
        {
            return null;
        }

        return new EvalOptions
        {
            RepoPath = repo,
            Holdout = holdout,
            Lookahead = lookahead,
            MaxEvalSize = maxEvalSize,
            Caps = caps,
            Floors = floors,
            CsvPath = csv,
        };
    }
}
