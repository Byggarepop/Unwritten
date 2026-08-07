using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Unwritten.Tool;

/// <summary>
/// Validates a tools/call request against the target tool's input schema before the SDK binds
/// the arguments to the tool method. Without this, a call missing <c>repoPath</c> (or sending
/// a misnamed parameter) dies inside the SDK's binding layer with the generic
/// "An error occurred invoking '&lt;tool&gt;'" — which gives a calling model nothing to
/// self-correct from. This returns an error naming the missing/invalid parameter and the
/// expected schema instead, and rescues predictable synonyms (like 'repo_path' for
/// 'repoPath') by aliasing them to the canonical name.
/// </summary>
public static class ToolCallValidation
{
    /// <summary>
    /// Canonical parameter name → synonyms models predictably send instead. A synonym is only
    /// applied when the canonical name is a declared property of the target tool's schema, the
    /// canonical name is absent from the call, and the synonym is not itself a declared
    /// property — so the table is safe to apply to every tool.
    /// </summary>
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.Ordinal)
    {
        ["repoPath"] = ["repo_path", "repo", "repository", "path"],
        ["minConfidence"] = ["min_confidence"],
        ["baseRef"] = ["base_ref"],
        ["fileA"] = ["file_a"],
        ["fileB"] = ["file_b"],
    };

    /// <summary>Wraps the next tools/call handler. Pass to <c>AddCallToolFilter</c>.</summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Attach(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) => async (context, cancellationToken) =>
    {
        var toolName = context.Params?.Name;
        var tools = context.Server.ServerOptions.ToolCollection;
        if (toolName is null || tools is null || !tools.TryGetPrimitive(toolName, out var tool))
        {
            // Unknown tool — let the SDK produce its own (already descriptive) error.
            return await next(context, cancellationToken);
        }

        var (error, normalized, aliased) = ValidateAndNormalize(
            tool.ProtocolTool.InputSchema, context.Params!.Arguments);

        var logger = context.Server.Services?.GetService<ILoggerFactory>()
            ?.CreateLogger("Unwritten.ToolCallValidation");
        foreach (var (synonym, canonical) in aliased)
        {
            logger?.LogWarning(
                "tools/call '{Tool}': parameter '{Synonym}' accepted as an alias for '{Canonical}'.",
                toolName, synonym, canonical);
        }

        if (error is not null)
        {
            logger?.LogWarning("tools/call '{Tool}' rejected: {Error}", toolName, error);
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = error }],
            };
        }

        context.Params.Arguments = normalized;

        var result = await next(context, cancellationToken);

        if (aliased.Count > 0)
        {
            var note = string.Join(" ", aliased.Select(a =>
                $"Note: parameter '{a.Synonym}' was accepted as an alias for '{a.Canonical}' — use '{a.Canonical}' in future calls."));
            result.Content = [.. result.Content ?? [], new TextContentBlock { Text = note }];
        }

        return result;
    };

    /// <summary>
    /// Checks the call's arguments against the tool's JSON input schema. Returns the aliased
    /// (normalized) argument map, the aliases applied, and — when the call cannot succeed — an
    /// error message naming the missing/unknown/wrongly-typed parameters and the expected
    /// shape. A schema without declared properties validates nothing.
    /// </summary>
    public static (string? Error, IDictionary<string, JsonElement>? Arguments, IReadOnlyList<(string Synonym, string Canonical)> Aliased)
        ValidateAndNormalize(JsonElement inputSchema, IDictionary<string, JsonElement>? arguments)
    {
        if (inputSchema.ValueKind != JsonValueKind.Object
            || !inputSchema.TryGetProperty("properties", out var propsElement)
            || propsElement.ValueKind != JsonValueKind.Object)
        {
            return (null, arguments, []);
        }

        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in propsElement.EnumerateObject())
        {
            properties[property.Name] = property.Value;
        }

        var required = new List<string>();
        if (inputSchema.TryGetProperty("required", out var requiredElement)
            && requiredElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in requiredElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String)
                {
                    required.Add(entry.GetString()!);
                }
            }
        }

        var args = arguments is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(arguments, StringComparer.Ordinal);

        var aliased = new List<(string Synonym, string Canonical)>();
        foreach (var (canonical, synonyms) in Aliases)
        {
            if (!properties.ContainsKey(canonical) || args.ContainsKey(canonical))
            {
                continue;
            }

            foreach (var synonym in synonyms)
            {
                if (properties.ContainsKey(synonym) || !args.TryGetValue(synonym, out var value))
                {
                    continue;
                }

                args.Remove(synonym);
                args[canonical] = value;
                aliased.Add((synonym, canonical));
                break;
            }
        }

        var unknown = args.Keys.Where(k => !properties.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var missing = required.Where(r => !args.ContainsKey(r)).ToList();
        var typeErrors = new List<string>();
        foreach (var (key, value) in args)
        {
            if (properties.TryGetValue(key, out var propSchema)
                && DeclaredType(propSchema) is { } declared
                && !Matches(declared, value.ValueKind))
            {
                typeErrors.Add($"Parameter '{key}' must be {Article(declared)} (received {Article(ReceivedType(value.ValueKind))}).");
            }
        }

        var error = missing.Count > 0 || unknown.Count > 0 || typeErrors.Count > 0
            ? ComposeError(missing, unknown, typeErrors, properties, required)
            : null;

        return (error, args, aliased);
    }

    /// <summary>Builds the full error sentence, always ending with the expected shape.</summary>
    private static string ComposeError(
        List<string> missing,
        List<string> unknown,
        List<string> typeErrors,
        Dictionary<string, JsonElement> properties,
        List<string> required)
    {
        var sb = new StringBuilder();
        if (missing.Count > 0)
        {
            sb.Append(missing.Count == 1
                ? $"Missing required parameter {Quote(missing)}"
                : $"Missing required parameters {Quote(missing)}");
            if (unknown.Count > 0)
            {
                sb.Append(unknown.Count == 1
                    ? $" (received unknown parameter {Quote(unknown)})"
                    : $" (received unknown parameters {Quote(unknown)})");
            }

            sb.Append('.');
        }
        else if (unknown.Count > 0)
        {
            sb.Append(unknown.Count == 1
                ? $"Unknown parameter {Quote(unknown)}."
                : $"Unknown parameters {Quote(unknown)}.");
        }

        foreach (var typeError in typeErrors)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(typeError);
        }

        sb.Append(" Expected shape: {");
        sb.Append(string.Join(", ", properties.Select(p =>
            $"{p.Key}{(required.Contains(p.Key) ? string.Empty : "?")}: {DeclaredType(p.Value) ?? "object"}")));
        sb.Append("}.");
        return sb.ToString();
    }

    private static string Quote(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"'{n}'"));

    /// <summary>The schema-declared type of a property, or null when the schema does not constrain it.</summary>
    private static string? DeclaredType(JsonElement propSchema)
    {
        if (propSchema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (propSchema.TryGetProperty("type", out var type))
        {
            // Nullable optionals render as ["string","null"] — report the non-null branch.
            if (type.ValueKind == JsonValueKind.String)
            {
                return type.GetString();
            }

            if (type.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in type.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.String && entry.GetString() != "null")
                    {
                        return entry.GetString();
                    }
                }
            }
        }

        return null;
    }

    private static bool Matches(string declaredType, JsonValueKind kind) => declaredType switch
    {
        // Optionals accept an explicit null alongside the declared type.
        _ when kind == JsonValueKind.Null => true,
        "string" => kind == JsonValueKind.String,
        "number" or "integer" => kind == JsonValueKind.Number,
        "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
        "object" => kind == JsonValueKind.Object,
        "array" => kind == JsonValueKind.Array,
        _ => true,
    };

    private static string ReceivedType(JsonValueKind kind) => kind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        _ => "null",
    };

    private static string Article(string type) => type switch
    {
        "object" or "array" or "integer" => $"an {type}",
        "null" => "null",
        _ => $"a {type}",
    };
}
