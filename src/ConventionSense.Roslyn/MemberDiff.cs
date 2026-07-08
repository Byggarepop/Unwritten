using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ConventionSense.Roslyn;

/// <summary>
/// Syntactic member-level diff of two versions of a C# file. Member identity is
/// <c>Namespace.Type.Member/arity</c> (nested types joined with <c>+</c>, no file
/// path — identity survives file moves). A member counts as changed when its
/// trivia-stripped token stream differs, so whitespace/comment-only edits change
/// nothing. Syntax only: no compilation, no semantic model.
/// </summary>
public static class MemberDiff
{
    /// <summary>
    /// Ids of members that differ between the two versions (added, removed, or
    /// modified). Null side = file absent (all members of the other side changed).
    /// Returns null when a non-null side fails to parse — callers fail open.
    /// </summary>
    public static IReadOnlySet<string>? ChangedMembers(string? before, string? after)
    {
        var beforeMembers = before is null ? new Dictionary<string, string>(StringComparer.Ordinal) : Extract(before);
        var afterMembers = after is null ? new Dictionary<string, string>(StringComparer.Ordinal) : Extract(after);
        if (beforeMembers is null || afterMembers is null)
        {
            return null;
        }

        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in beforeMembers.Keys.Union(afterMembers.Keys, StringComparer.Ordinal))
        {
            if (beforeMembers.GetValueOrDefault(id) != afterMembers.GetValueOrDefault(id))
            {
                changed.Add(id);
            }
        }

        return changed;
    }

    /// <summary>All member ids present in one version of a file (null on parse failure).</summary>
    public static IReadOnlySet<string>? Members(string source)
    {
        var members = Extract(source);
        return members is null ? null : members.Keys.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Member id → normalized (trivia-free) token text.</summary>
    private static Dictionary<string, string>? Extract(string source)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        var root = (CompilationUnitSyntax)tree.GetRoot();

        // "Failed to parse" for our purposes = error density suggesting non-C# or
        // truncated content. Roslyn always produces a tree, so use diagnostics.
        if (root.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return null;
        }

        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(root.Members, namespacePrefix: "", typePrefix: "", members);

        // Top-level statements (Program.cs style): one synthetic entity. No file
        // path in ids means top-level programs in different files would collide —
        // rare enough to accept and document.
        var topLevel = root.Members.OfType<GlobalStatementSyntax>().ToList();
        if (topLevel.Count > 0)
        {
            members["<TopLevelProgram>"] = NormalizedText([.. topLevel]);
        }

        return members;
    }

    private static void Walk(
        IEnumerable<MemberDeclarationSyntax> declarations, string namespacePrefix, string typePrefix,
        Dictionary<string, string> members)
    {
        foreach (var declaration in declarations)
        {
            switch (declaration)
            {
                case BaseNamespaceDeclarationSyntax ns:
                    string nsName = Combine(namespacePrefix, ns.Name.ToString(), ".");
                    Walk(ns.Members, nsName, typePrefix, members);
                    break;

                case TypeDeclarationSyntax type: // class/struct/interface/record
                {
                    string typeName = Combine(typePrefix, TypeDisplayName(type), "+");
                    string typeId = Combine(namespacePrefix, typeName, ".");

                    // The type "header" (attributes, modifiers, base list, constraints)
                    // is its own entity so non-member changes are still visible.
                    // Partial declarations concatenate (sorted by text for determinism).
                    string header = NormalizedText(HeaderTokens(type));
                    members[typeId] = members.TryGetValue(typeId, out var existing)
                        ? string.Join("", new[] { existing, header }.Order(StringComparer.Ordinal))
                        : header;

                    foreach (var member in type.Members)
                    {
                        AddMember(member, typeId, typeName, namespacePrefix, members);
                    }

                    break;
                }

                case EnumDeclarationSyntax enumType:
                {
                    string enumId = Combine(namespacePrefix, Combine(typePrefix, enumType.Identifier.Text, "+"), ".");
                    members[enumId] = NormalizedText([enumType]);
                    break;
                }

                case DelegateDeclarationSyntax del:
                {
                    string delId = Combine(namespacePrefix, Combine(typePrefix, del.Identifier.Text, "+"), ".");
                    members[delId] = NormalizedText([del]);
                    break;
                }
            }
        }
    }

    private static void AddMember(
        MemberDeclarationSyntax member, string typeId, string typeName, string namespacePrefix,
        Dictionary<string, string> members)
    {
        switch (member)
        {
            case TypeDeclarationSyntax or EnumDeclarationSyntax or DelegateDeclarationSyntax:
                Walk([member], namespacePrefix, typeName, members);
                return;

            case MethodDeclarationSyntax method:
                Add($"{typeId}.{method.Identifier.Text}/{method.ParameterList.Parameters.Count}", member);
                return;

            case ConstructorDeclarationSyntax ctor:
                Add($"{typeId}..ctor/{ctor.ParameterList.Parameters.Count}", member);
                return;

            case DestructorDeclarationSyntax:
                Add($"{typeId}..dtor/0", member);
                return;

            case PropertyDeclarationSyntax property:
                Add($"{typeId}.{property.Identifier.Text}/0", member);
                return;

            case IndexerDeclarationSyntax indexer:
                Add($"{typeId}.this[]/{indexer.ParameterList.Parameters.Count}", member);
                return;

            case EventDeclarationSyntax @event:
                Add($"{typeId}.{@event.Identifier.Text}/0", member);
                return;

            case EventFieldDeclarationSyntax eventField:
                foreach (var variable in eventField.Declaration.Variables)
                {
                    // Per-variable text so `int f, g;` doesn't mark g when only f changes.
                    AddText($"{typeId}.{variable.Identifier.Text}/0",
                        NormalizedText([eventField.Declaration.Type, variable]));
                }

                return;

            case FieldDeclarationSyntax field:
                foreach (var variable in field.Declaration.Variables)
                {
                    AddText($"{typeId}.{variable.Identifier.Text}/0",
                        NormalizedText([field.Declaration.Type, variable]));
                }

                return;

            case OperatorDeclarationSyntax op:
                Add($"{typeId}.op_{op.OperatorToken.Text}/{op.ParameterList.Parameters.Count}", member);
                return;

            case ConversionOperatorDeclarationSyntax conversion:
                Add($"{typeId}.op_conversion_{conversion.Type}/{conversion.ParameterList.Parameters.Count}", member);
                return;
        }

        void Add(string id, MemberDeclarationSyntax declaration)
        {
            AddText(id, NormalizedText([(SyntaxNode)declaration]));
        }

        void AddText(string id, string text)
        {
            // Same-arity overloads (and partial methods) collapse: concatenate
            // deterministically so any overload's change marks the entity.
            members[id] = members.TryGetValue(id, out var existing)
                ? string.Join("", new[] { existing, text }.Order(StringComparer.Ordinal))
                : text;
        }
    }

    private static string TypeDisplayName(TypeDeclarationSyntax type) =>
        type.TypeParameterList is { Parameters.Count: > 0 and var count }
            ? $"{type.Identifier.Text}`{count}"
            : type.Identifier.Text;

    private static IEnumerable<SyntaxNodeOrToken> HeaderTokens(TypeDeclarationSyntax type)
    {
        foreach (var attribute in type.AttributeLists)
        {
            yield return attribute;
        }

        foreach (var modifier in type.Modifiers)
        {
            yield return modifier;
        }

        yield return type.Keyword;
        yield return type.Identifier;
        if (type.TypeParameterList is not null)
        {
            yield return type.TypeParameterList;
        }

        if (type.ParameterList is not null) // primary constructor
        {
            yield return type.ParameterList;
        }

        if (type.BaseList is not null)
        {
            yield return type.BaseList;
        }

        foreach (var constraint in type.ConstraintClauses)
        {
            yield return constraint;
        }
    }

    private static string NormalizedText(IEnumerable<SyntaxNodeOrToken> nodes) =>
        string.Join("\0", nodes.SelectMany(TokensOf).Select(t => t.Text));

    private static IEnumerable<SyntaxToken> TokensOf(SyntaxNodeOrToken nodeOrToken) =>
        nodeOrToken.IsToken ? [nodeOrToken.AsToken()] : nodeOrToken.AsNode()!.DescendantTokens();

    private static string Combine(string prefix, string name, string separator) =>
        prefix.Length == 0 ? name : prefix + separator + name;
}
