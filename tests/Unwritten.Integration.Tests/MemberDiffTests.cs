using Unwritten.Roslyn;

namespace Unwritten.Integration.Tests;

public class MemberDiffTests
{
    [Fact]
    public void DetectsChangedMethodBody()
    {
        var changed = MemberDiff.ChangedMembers(
            """
            namespace MyApp;
            public class Svc
            {
                public int Add(int a, int b) => a + b;
                public int Sub(int a, int b) => a - b;
            }
            """,
            """
            namespace MyApp;
            public class Svc
            {
                public int Add(int a, int b) => a + b + 0;
                public int Sub(int a, int b) => a - b;
            }
            """);

        Assert.Equal(["MyApp.Svc.Add/2"], changed!);
    }

    [Fact]
    public void CommentAndWhitespaceOnlyEditsChangeNothing()
    {
        var changed = MemberDiff.ChangedMembers(
            """
            namespace MyApp;
            public class Svc
            {
                public int Add(int a, int b) => a + b;
            }
            """,
            """
            namespace MyApp;

            public class Svc
            {
                // this method adds two numbers
                public int Add(int a, int b)   =>   a + b;   /* really */
            }
            """);

        Assert.Empty(changed!);
    }

    [Fact]
    public void OverloadsWithDifferentArityAreDistinct()
    {
        var changed = MemberDiff.ChangedMembers(
            """
            public class C
            {
                public void M(int a) { }
                public void M(int a, int b) { }
            }
            """,
            """
            public class C
            {
                public void M(int a) { }
                public void M(int a, int b) { var x = 1; }
            }
            """);

        Assert.Equal(["C.M/2"], changed!);
    }

    [Fact]
    public void AddedAndRemovedMembersAreChanges()
    {
        var changed = MemberDiff.ChangedMembers(
            """public class C { public void Old() { } }""",
            """public class C { public void New() { } }""");

        Assert.Equal(["C.New/0", "C.Old/0"], changed!.Order());
    }

    [Fact]
    public void NestedTypesUsePlusSeparator()
    {
        var changed = MemberDiff.ChangedMembers(
            """
            namespace N { public class Outer { public class Inner { public int F = 1; } } }
            """,
            """
            namespace N { public class Outer { public class Inner { public int F = 2; } } }
            """);

        Assert.Equal(["N.Outer+Inner.F/0"], changed!);
    }

    [Fact]
    public void PropertiesAndFieldsAreMembers()
    {
        var changed = MemberDiff.ChangedMembers(
            """public class C { public int P { get; set; } public int f, g; }""",
            """public class C { public int P { get; init; } public int f = 5, g; }""");

        Assert.Equal(["C.P/0", "C.f/0"], changed!.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void BaseListChangeMarksTheTypeEntity()
    {
        var changed = MemberDiff.ChangedMembers(
            """public class C { public void M() { } }""",
            """public class C : IDisposable { public void M() { } public void Dispose() { } }""");

        Assert.Equal(["C", "C.Dispose/0"], changed!.Order());
    }

    [Fact]
    public void FileCreationMarksAllMembers()
    {
        var changed = MemberDiff.ChangedMembers(
            null,
            """public class C { public void M() { } }""");

        Assert.Equal(["C", "C.M/0"], changed!.Order());
    }

    [Fact]
    public void ParseErrorIsUntrackable()
    {
        Assert.Null(MemberDiff.ChangedMembers("public class C {", "public class C { }"));
    }

    [Fact]
    public void GenericTypesCarryArityMarker()
    {
        var changed = MemberDiff.ChangedMembers(
            """public class Repo<T> { public T Get(int id) => default!; }""",
            """public class Repo<T> { public T Get(int id) => throw new System.Exception(); }""");

        Assert.Equal(["Repo`1.Get/1"], changed!);
    }

    [Fact]
    public void TopLevelStatementsRollUpToOneEntity()
    {
        var changed = MemberDiff.ChangedMembers(
            """System.Console.WriteLine("a");""",
            """System.Console.WriteLine("b");""");

        Assert.Equal(["<TopLevelProgram>"], changed!);
    }
}
