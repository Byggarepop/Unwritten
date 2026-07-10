using Unwritten.Git;
using Unwritten.Storage;
using Unwritten.Tool;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var runner = new GitRunner();
var gitSource = new GitTransactionSource(runner);
var indexManager = new IndexManager(gitSource);

switch (args.FirstOrDefault())
{
    case "mcp":
        await RunMcpServerAsync(args[1..]);
        return 0;
    case "check":
        return RunCli(() => CheckCommand.Run(args[1..], indexManager, gitSource, Console.Out));
    case "stats":
        return RunCli(() => StatsCommand.Run(args[1..], indexManager, Console.Out, rebuild: false));
    case "reindex":
        return RunCli(() => StatsCommand.Run(args[1..], indexManager, Console.Out, rebuild: true));
    default:
        Console.Error.WriteLine("""
            Unwritten — git-history-based hole detector.

            Usage:
              unwritten mcp                     Run as MCP server over stdio.
              unwritten check [options] [files] Check files for co-change holes.
              unwritten stats [--repo <path>]   Show index health and rule counts.
              unwritten reindex [--repo <path>] Rebuild the index from full history.

            Check options:
              --staged                Check the currently staged files.
              --repo <path>           Repository root (default: current directory).
              --min-confidence <x>    Report floor, Wilson lower bound (default 0.6).
              --fail-at <x>           Exit 1 if any hole reaches this confidence (default 0.7).
              --strict                Treat content-suppressed holes as real holes.

            Defaults can be overridden per repo in .unwritten/config.json.
            """);
        return 2;
}

static int RunCli(Func<int> command)
{
    try
    {
        return command();
    }
    catch (GitException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 2;
    }
}

async Task RunMcpServerAsync(string[] mcpArgs)
{
    var builder = Host.CreateApplicationBuilder(mcpArgs);

    // stdio transport: stdout belongs to the protocol, so all logging goes to stderr.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

    builder.Services.AddSingleton(runner);
    builder.Services.AddSingleton(gitSource);
    builder.Services.AddSingleton(indexManager);
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithTools<UnwrittenTools>();

    await builder.Build().RunAsync();
}
