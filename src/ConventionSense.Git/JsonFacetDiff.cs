using System.Text.Json;

namespace ConventionSense.Git;

/// <summary>
/// Structural diff of two JSON documents into a set of changed facet paths
/// (e.g. <c>info.version</c>, <c>packages[].name</c>). Array indices collapse
/// to <c>[]</c>, paths are depth-capped, and a whole-document change at the
/// root reports <c>$</c>. Pure content-in, facets-out — the JSON counterpart
/// of the git adapter, mapping raw content onto the core's neutral facets.
/// </summary>
public static class JsonFacetDiff
{
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Facet paths that differ between the two documents. Returns null when either
    /// side is not valid JSON or the result exceeds <paramref name="maxFacets"/> —
    /// callers must treat null as "untrackable" and fail open.
    /// </summary>
    public static IReadOnlySet<string>? ChangedFacets(
        string beforeJson, string afterJson, int maxDepth, int maxFacets)
    {
        JsonDocument before;
        JsonDocument after;
        try
        {
            before = JsonDocument.Parse(beforeJson, ParseOptions);
            after = JsonDocument.Parse(afterJson, ParseOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        using (before)
        using (after)
        {
            var changed = new HashSet<string>(StringComparer.Ordinal);
            Diff(before.RootElement, after.RootElement, path: "", depth: 0, maxDepth, changed);
            return changed.Count > maxFacets ? null : changed;
        }
    }

    private static void Diff(
        JsonElement a, JsonElement b, string path, int depth, int maxDepth, HashSet<string> changed)
    {
        if (JsonElement.DeepEquals(a, b))
        {
            return;
        }

        bool canRecurse = depth < maxDepth &&
            a.ValueKind == b.ValueKind &&
            (a.ValueKind is JsonValueKind.Object or JsonValueKind.Array);

        if (!canRecurse)
        {
            changed.Add(FacetName(path));
            return;
        }

        if (a.ValueKind == JsonValueKind.Object)
        {
            var propertiesA = ToPropertyMap(a);
            var propertiesB = ToPropertyMap(b);
            foreach (var name in propertiesA.Keys.Union(propertiesB.Keys, StringComparer.Ordinal))
            {
                string childPath = Join(path, name);
                bool inA = propertiesA.TryGetValue(name, out var valueA);
                bool inB = propertiesB.TryGetValue(name, out var valueB);
                if (inA && inB)
                {
                    Diff(valueA, valueB, childPath, depth + 1, maxDepth, changed);
                }
                else
                {
                    // Added or removed property: the key itself changed.
                    changed.Add(FacetName(childPath));
                }
            }
        }
        else
        {
            // Arrays: indices are noise; compare index-aligned elements under the
            // collapsed "[]" segment (which does not consume a depth level), and
            // treat appended/removed elements as changes of everything they contain.
            string elementPath = path + "[]";
            int common = Math.Min(a.GetArrayLength(), b.GetArrayLength());
            for (int i = 0; i < common; i++)
            {
                Diff(a[i], b[i], elementPath, depth, maxDepth, changed);
            }

            var longer = a.GetArrayLength() > common ? a : b;
            for (int i = common; i < longer.GetArrayLength(); i++)
            {
                AddAllPaths(longer[i], elementPath, depth, maxDepth, changed);
            }
        }
    }

    private static void AddAllPaths(
        JsonElement element, string path, int depth, int maxDepth, HashSet<string> changed)
    {
        if (depth < maxDepth && element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                AddAllPaths(property.Value, Join(path, property.Name), depth + 1, maxDepth, changed);
            }
        }
        else if (depth < maxDepth && element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                AddAllPaths(item, path + "[]", depth, maxDepth, changed);
            }
        }
        else
        {
            changed.Add(FacetName(path));
        }
    }

    private static Dictionary<string, JsonElement> ToPropertyMap(JsonElement element)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            map[property.Name] = property.Value; // duplicate keys: last wins, per JSON convention
        }

        return map;
    }

    private static string Join(string path, string name) =>
        path.Length == 0 ? name : path + "." + name;

    private static string FacetName(string path) => path.Length == 0 ? "$" : path;
}
