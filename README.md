# Unwritten

[![NuGet](https://img.shields.io/nuget/v/Unwritten.svg)](https://www.nuget.org/packages/Unwritten) [![Downloads](https://img.shields.io/nuget/dt/Unwritten.svg)](https://www.nuget.org/packages/Unwritten) [![License: MIT](https://img.shields.io/github/license/Byggarepop/Unwritten.svg)](https://github.com/Byggarepop/Unwritten/blob/main/LICENSE)

**The free, agent-native slice of change coupling.** Unwritten learns from your git
history which files are expected to change together, and flags statistically
confident *absences*: "you changed `OrderService.cs` but not `OrderServiceTests.cs`, and they co-change 94% of the time."

It runs as an **MCP server** so AI coding agents (Claude Code, Copilot) can check
their own edits for holes mid-session, and as a **CLI** for pre-commit hooks.
One `dotnet tool execute`, an index in `.unwritten/`, no server, no subscription,
no tokens.

**Works on any language.** File-level rules only need git history, so hole
detection works the same on Python, TypeScript, Go, or mixed repos. C# repos
additionally get method-level rules and cosmetic-edit filtering; JSON files get
key-level noise filtering. Running the tool requires the
[.NET SDK](https://dotnet.microsoft.com/download) (10+), but the repos it
analyzes can be anything.

## Quick start

From your repo's root:

1. Warm up the index (optional — every command builds it on first use and keeps it current by itself; this just makes the first query fast).

```bash
dotnet tool execute Unwritten --yes -- reindex
```

2. Register as an MCP server (Claude Code):

```bash
claude mcp add unwritten -- dotnet tool execute Unwritten --yes -- mcp
```

3. (Recommended if using Claude Code) Make the check deterministic — a git pre-commit hook and a Claude Code Stop hook that feeds failing holes back to the agent before a commit is made:

```bash
dotnet tool execute Unwritten --yes -- install-hook --git --claude-code
```

That's it — your agent can now call `check_holes` after editing, and the hooks
catch the cases where it forgets to.

## Documentation

Everything else lives in **[docs/README.md](docs/README.md)**:

- [Background & the research behind it](docs/README.md#this-is-not-a-new-idea--and-thats-the-point)
- [Why these thresholds (tested on real data)](docs/README.md#why-these-thresholds-tested-on-real-data)
- [Use: MCP server, CLI, hooks, muting false rules](docs/README.md#use)
- [Configuration reference](docs/README.md#configuration--unwrittenconfigjson)
- [How it works](docs/README.md#how-it-works)
- [What it does NOT do (yet)](docs/README.md#what-it-does-not-do-yet)

## License

[MIT](LICENSE)
