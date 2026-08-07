using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Unwritten.Tool;

namespace Unwritten.Integration.Tests;

/// <summary>
/// Tests for the pre-binding tools/call validation: a call with missing, misnamed, or
/// wrongly-typed parameters must return an error naming the exact problem and the expected
/// schema — not the SDK's generic "An error occurred invoking '&lt;tool&gt;'" — and
/// predictable synonyms like 'repo_path' must be rescued by aliasing.
/// </summary>
public sealed class McpToolValidationTests
{
    /// <summary>The check_holes schema shape as the SDK generates it.</summary>
    private static readonly JsonElement CheckHolesSchema = Json("""
        {
          "type": "object",
          "properties": {
            "repoPath": { "type": "string" },
            "files": { "type": "array" },
            "minConfidence": { "type": "number" },
            "baseRef": { "type": "string" }
          },
          "required": ["repoPath"]
        }
        """);

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, JsonElement> Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    [Fact]
    public void Missing_repoPath_is_named_with_expected_shape()
    {
        var (error, _, _) = ToolCallValidation.ValidateAndNormalize(CheckHolesSchema, Args("{}"));

        Assert.Equal(
            "Missing required parameter 'repoPath'. Expected shape: " +
            "{repoPath: string, files?: array, minConfidence?: number, baseRef?: string}.",
            error);
    }

    [Theory]
    [InlineData("repo_path")]
    [InlineData("repo")]
    [InlineData("repository")]
    [InlineData("path")]
    public void Synonyms_for_repoPath_are_aliased(string synonym)
    {
        var (error, args, aliased) = ToolCallValidation.ValidateAndNormalize(
            CheckHolesSchema, Args($$"""{ "{{synonym}}": "C:/repo" }"""));

        Assert.Null(error);
        Assert.Equal((synonym, "repoPath"), Assert.Single(aliased));
        Assert.Equal("C:/repo", args!["repoPath"].GetString());
        Assert.False(args.ContainsKey(synonym));
    }

    [Fact]
    public void Wrongly_typed_parameter_is_named_with_both_types()
    {
        var (error, _, _) = ToolCallValidation.ValidateAndNormalize(
            CheckHolesSchema, Args("""{ "repoPath": "C:/repo", "files": "a.cs" }"""));

        Assert.Equal(
            "Parameter 'files' must be an array (received a string). Expected shape: " +
            "{repoPath: string, files?: array, minConfidence?: number, baseRef?: string}.",
            error);
    }

    [Fact]
    public void Unknown_parameter_is_named()
    {
        var (error, _, _) = ToolCallValidation.ValidateAndNormalize(
            CheckHolesSchema, Args("""{ "repoPath": "C:/repo", "bogus": 1 }"""));

        Assert.StartsWith("Unknown parameter 'bogus'.", error);
    }

    [Fact]
    public async Task Check_holes_without_repoPath_over_mcp_returns_descriptive_error()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        // UnwrittenTools' dependencies are deliberately NOT registered: an invalid call must be
        // rejected by the validation filter before the SDK ever binds or constructs the tool.
        builder.Services
            .AddMcpServer()
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithTools<UnwrittenTools>()
            .WithRequestFilters(filters => filters.AddCallToolFilter(ToolCallValidation.Attach));

        var host = builder.Build();
        try
        {
            await host.StartAsync();
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(), serverOutput: serverToClient.Reader.AsStream()));

            var result = await client.CallToolAsync("check_holes", new Dictionary<string, object?>());

            Assert.True(result.IsError);
            var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
            Assert.Contains("Missing required parameter 'repoPath'", text);
            Assert.Contains("Expected shape:", text);
            Assert.DoesNotContain("An error occurred invoking", text);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
}
