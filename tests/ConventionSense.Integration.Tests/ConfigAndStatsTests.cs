using ConventionSense.Git;
using ConventionSense.Storage;
using ConventionSense.Tool;

namespace ConventionSense.Integration.Tests;

public class ConfigAndStatsTests : IDisposable
{
    private readonly SyntheticRepo _repo = new();
    private readonly GitTransactionSource _source = new(new GitRunner());
    private readonly IndexManager _manager;

    public ConfigAndStatsTests()
    {
        _manager = new IndexManager(_source);
    }

    public void Dispose() => _repo.Dispose();

    private void WriteConfig(string json)
    {
        Directory.CreateDirectory(Path.Combine(_repo.Path, ".conventionsense"));
        File.WriteAllText(ConventionSenseConfig.GetConfigPath(_repo.Path), json);
    }

    [Fact]
    public void MissingConfigFileYieldsDefaults()
    {
        var config = ConventionSenseConfig.Load(_repo.Path);

        Assert.Equal(10, config.MinSupport);
        Assert.Equal(30, config.MaxTransactionSize);
        Assert.Equal(0.6, config.DefaultMinConfidence);
        Assert.Equal(0.7, config.FailConfidence);
    }

    [Fact]
    public void PartialConfigFileOverridesOnlyGivenSettings()
    {
        WriteConfig("""{ "maxTransactionSize": 2, "failConfidence": 0.9 }""");

        var config = ConventionSenseConfig.Load(_repo.Path);

        Assert.Equal(2, config.MaxTransactionSize);
        Assert.Equal(0.9, config.FailConfidence);
        Assert.Equal(10, config.MinSupport); // untouched default
    }

    [Fact]
    public void ConfigAppliesToIndexBuild()
    {
        WriteConfig("""{ "maxTransactionSize": 2 }""");
        _repo.Commit("a.txt", "b.txt");
        _repo.Commit("a.txt", "b.txt", "c.txt"); // 3 files > limit 2 → excluded

        var persisted = _manager.GetUpToDate(_repo.Path);

        Assert.Equal(1, persisted.Index.TransactionCount);
        Assert.Equal(2, persisted.Index.Config.MaxTransactionSize);
    }

    [Fact]
    public void ReindexAppliesChangedConfig()
    {
        _repo.Commit("a.txt", "b.txt", "c.txt");
        var before = _manager.GetUpToDate(_repo.Path);
        Assert.Equal(1, before.Index.TransactionCount); // 3 files ≤ default 30

        WriteConfig("""{ "maxTransactionSize": 2 }""");
        var after = _manager.Rebuild(_repo.Path);

        Assert.Equal(0, after.Index.TransactionCount);

        // GetUpToDate must now serve the rebuilt index, not a stale cache.
        Assert.Equal(0, _manager.GetUpToDate(_repo.Path).Index.TransactionCount);
    }

    [Fact]
    public void CheckCommandUsesConfiguredFailFloor()
    {
        // 15/16 co-changes → confidence ≈ 0.72: fails at default 0.7,
        // passes once config raises the fail floor.
        for (int i = 0; i < 15; i++)
        {
            _repo.Commit("api.cs", "api.contract.cs");
        }

        _repo.Commit("api.cs");

        using var writer = new StringWriter();
        Assert.Equal(1, CheckCommand.Run(["api.cs", "--repo", _repo.Path], _manager, _source, writer));

        WriteConfig("""{ "failConfidence": 0.9 }""");
        Assert.Equal(0, CheckCommand.Run(["api.cs", "--repo", _repo.Path], _manager, _source, writer));

        // Explicit flag still overrides the config file.
        Assert.Equal(1, CheckCommand.Run(
            ["api.cs", "--repo", _repo.Path, "--fail-at", "0.7"], _manager, _source, writer));
    }

    [Fact]
    public void CheckCommandUsesConfiguredReportFloor()
    {
        // Confidence ≈ 0.24 (6/12): invisible at the default 0.6 floor,
        // reported when the config lowers it.
        for (int i = 0; i < 6; i++)
        {
            _repo.Commit("x.txt", "y.txt");
        }

        for (int i = 0; i < 6; i++)
        {
            _repo.Commit("x.txt");
        }

        using var writer = new StringWriter();
        CheckCommand.Run(["x.txt", "--repo", _repo.Path], _manager, _source, writer);
        Assert.Contains("No holes", writer.ToString());

        WriteConfig("""{ "defaultMinConfidence": 0.2, "failConfidence": 0.99 }""");
        using var writer2 = new StringWriter();
        Assert.Equal(0, CheckCommand.Run(["x.txt", "--repo", _repo.Path], _manager, _source, writer2));
        Assert.Contains("y.txt", writer2.ToString());
    }

    [Fact]
    public void StatsReportCountsRules()
    {
        for (int i = 0; i < 12; i++)
        {
            _repo.Commit("a.txt", "b.txt");
        }

        var persisted = _manager.GetUpToDate(_repo.Path);
        var report = StatsReport.Build(_repo.Path, persisted);

        Assert.Equal(_repo.Head(), report.IndexedHead);
        Assert.Equal(12, report.TransactionCount);
        Assert.Equal(2, report.EntityCount);
        Assert.Equal(1, report.PairCount);
        Assert.True(report.IndexFileBytes > 0);
        // wilson_lb(12,12) ≈ 0.74: both directions clear 0.5–0.7, none clear 0.8.
        Assert.Equal(2, report.RulesAtFloor["0.5"]);
        Assert.Equal(2, report.RulesAtFloor["0.6"]);
        Assert.Equal(2, report.RulesAtFloor["0.7"]);
        Assert.Equal(0, report.RulesAtFloor["0.8"]);
    }

    [Fact]
    public void StatsCommandPrintsReportAndReindexRebuilds()
    {
        _repo.Commit("a.txt", "b.txt");

        using var writer = new StringWriter();
        Assert.Equal(0, StatsCommand.Run(["--repo", _repo.Path], _manager, writer, rebuild: false));
        Assert.Contains("transactions:  1", writer.ToString());

        WriteConfig("""{ "maxTransactionSize": 1 }""");
        using var writer2 = new StringWriter();
        Assert.Equal(0, StatsCommand.Run(["--repo", _repo.Path], _manager, writer2, rebuild: true));
        Assert.Contains("Index rebuilt", writer2.ToString());
        Assert.Contains("transactions:  0", writer2.ToString());
    }
}
