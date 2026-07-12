using System.Reflection;
using Unwritten.Git;
using Unwritten.Storage;
using Unwritten.Tool;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var runner = new GitRunner();
var gitSource = new GitTransactionSource(runner);
var indexManager = new IndexManager(gitSource);

const string usage = """
    Unwritten — git-history-based hole detector.

    Usage:
      unwritten mcp                     Run as MCP server over stdio.
      unwritten check [options] [files] Check files for co-change holes.
                                        No files: checks all uncommitted changes.
      unwritten stats [--repo <path>]   Show index health and rule counts.
      unwritten reindex [--repo <path>] Rebuild the index from full history.
      unwritten install-hook [flags]    Install hooks that run the check
                                        deterministically. Flags: --git
                                        (pre-commit), --claude-code (Stop hook),
                                        --repo <path>, --force.
      unwritten ignore <trigger> <hole> Mute a false rule until the trigger has
                                        changed N more times (--for <n>,
                                        default 30). --list / --remove manage
                                        existing ignores.
      unwritten --version               Print the tool version.

    Check options:
      --staged                Check the currently staged files.
      --base <rev>            Measure changes against this revision instead of
                              HEAD (use after committing work in progress).
      --repo <path>           Repository root (default: current directory).
      --min-confidence <x>    Report floor, Wilson lower bound (default 0.6).
      --fail-at <x>           Exit 1 if any hole reaches this confidence (default 0.7).
      --strict                Treat content-suppressed holes as real holes.

    Defaults can be overridden per repo in .unwritten/config.json.
    """;

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
    case "install-hook":
        return RunCli(() => HookCommand.Install(args[1..], indexManager, gitSource, Console.Out));
    case "ignore":
        return RunCli(() => IgnoreCommand.Run(args[1..], indexManager, Console.Out));
    case "hook" when args.Length >= 2 && args[1] == "stop":
        // Fails open by design (exit 0 on any infrastructure problem): a broken
        // hook must never block the agent from finishing a turn.
        return HookCommand.Stop(
            indexManager, gitSource,
            Console.IsInputRedirected ? Console.In : TextReader.Null,
            Console.Error);
    case "--version" or "version":
        Console.WriteLine(
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown");
        return 0;
    case "--help" or "-h" or "help":
        Console.WriteLine(usage);
        return 0;
    default:
        Console.Error.WriteLine(usage);
        return 2;
}

static int RunCli(Func<int> command)
{
    try
    {
        return command();
    }
    catch (Exception ex) when (ex is GitException or ConfigException)
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
