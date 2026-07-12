using Unwritten.Tool;

namespace Unwritten.Integration.Tests;

public class EntityPathTests
{
    private static readonly string Repo = Path.Combine(Path.GetTempPath(), "unwritten-entitypath", "repo");

    [Fact]
    public void StripsLeadingDotSlash()
    {
        Assert.Equal("src/Foo.cs", EntityPath.Normalize(Repo, "./src/Foo.cs"));
        Assert.Equal("src/Foo.cs", EntityPath.Normalize(Repo, "././src/Foo.cs"));
    }

    [Fact]
    public void ConvertsBackslashes()
    {
        Assert.Equal("src/Foo.cs", EntityPath.Normalize(Repo, @"src\Foo.cs"));
    }

    [Fact]
    public void RelativizesAbsolutePathsInsideTheRepo()
    {
        string file = Path.Combine(Repo, "src", "Foo.cs");
        Assert.Equal("src/Foo.cs", EntityPath.Normalize(Repo, file));
    }

    [Fact]
    public void DoesNotRelativizeASiblingDirectoryWithACommonPrefix()
    {
        // "repo2" starts with "repo" — a raw prefix check would produce a bogus
        // "../repo2/..." entity id here.
        string sibling = Path.Combine(Path.GetTempPath(), "unwritten-entitypath", "repo2", "Foo.cs");
        string normalized = EntityPath.Normalize(Repo, sibling);

        Assert.DoesNotContain("..", normalized);
    }

    [Fact]
    public void MatchesRepoRootCaseInsensitivelyOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string file = Path.Combine(Repo.ToUpperInvariant(), "src", "Foo.cs");
        Assert.Equal("src/Foo.cs", EntityPath.Normalize(Repo, file));
    }
}
