using Unwritten.Git;
using Unwritten.Storage;

namespace Unwritten.Integration.Tests;

/// <summary>
/// Self-healing and configuration robustness: corrupt artifacts rebuild instead
/// of crashing, config edits take effect without manual reindexing, and bad
/// config fails loudly with a clear message.
/// </summary>
public class RobustnessTests : IDisposable
{
    private readonly SyntheticRepo _repo = new();
    private readonly GitTransactionSource _source = new(new GitRunner());
    private readonly IndexManager _manager;

    public RobustnessTests()
    {
        _manager = new IndexManager(_source);
    }

    public void Dispose() => _repo.Dispose();

    private void WriteConfig(string json)
    {
        Directory.CreateDirectory(Path.Combine(_repo.Path, ".unwritten"));
        File.WriteAllText(UnwrittenConfig.GetConfigPath(_repo.Path), json);
    }

    [Fact]
    public void CorruptIndexFileTriggersSilentRebuild()
    {
        _repo.Commit("a.txt", "b.txt");
        _repo.Commit("a.txt", "b.txt");
        _manager.GetUpToDate(_repo.Path);

        File.WriteAllText(IndexStore.GetIndexPath(_repo.Path), "{ this is not json");

        var rebuilt = new IndexManager(_source).GetUpToDate(_repo.Path);

        Assert.Equal(2, rebuilt.Index.TransactionCount);
    }

    [Fact]
    public void InvalidConfigJsonThrowsAClearError()
    {
        WriteConfig("{ this is not json");

        var ex = Assert.Throws<ConfigException>(() => UnwrittenConfig.Load(_repo.Path));
        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public void InvalidConfigValueThrowsAClearError()
    {
        WriteConfig("""{ "minSupport": 0 }""");

        var ex = Assert.Throws<ConfigException>(() => UnwrittenConfig.Load(_repo.Path));
        Assert.Contains("minSupport", ex.Message);
    }

    [Fact]
    public void TrainingConfigChangeTriggersAutomaticRebuild()
    {
        _repo.Commit("a.txt", "b.txt", "c.txt");
        Assert.Equal(1, _manager.GetUpToDate(_repo.Path).Index.TransactionCount);

        // No manual reindex: the same manager must notice the config edit.
        WriteConfig("""{ "maxTransactionSize": 2 }""");

        Assert.Equal(0, _manager.GetUpToDate(_repo.Path).Index.TransactionCount);
    }

    [Fact]
    public void FloorChangeAppliesWithoutRebuild()
    {
        for (int i = 0; i < 12; i++)
        {
            _repo.Commit("a.txt", "b.txt");
        }

        Assert.Equal(0.6, _manager.GetUpToDate(_repo.Path).Index.Config.DefaultMinConfidence);

        WriteConfig("""{ "defaultMinConfidence": 0.55 }""");
        var updated = _manager.GetUpToDate(_repo.Path);

        Assert.Equal(0.55, updated.Index.Config.DefaultMinConfidence);
        Assert.Equal(12, updated.Index.TransactionCount); // same index, new floor
    }

    [Fact]
    public void UnwrittenDirectoryIgnoresItself()
    {
        _repo.Commit("a.txt");
        _manager.GetUpToDate(_repo.Path);

        Assert.True(File.Exists(Path.Combine(_repo.Path, ".unwritten", ".gitignore")));
        Assert.DoesNotContain(".unwritten", _repo.Git("status", "--porcelain"));
    }

    [Fact]
    public void EmptyRepositoryGivesAFriendlyError()
    {
        var ex = Assert.Throws<GitException>(() => _manager.GetUpToDate(_repo.Path));
        Assert.Contains("no commits yet", ex.Message);
    }
}
