<!-- mcp-name: io.github.Byggarepop/unwritten -->

# Unwritten

[![NuGet](https://img.shields.io/nuget/v/Unwritten.svg)](https://www.nuget.org/packages/Unwritten) [![Downloads](https://img.shields.io/nuget/dt/Unwritten.svg)](https://www.nuget.org/packages/Unwritten) [![License: MIT](https://img.shields.io/github/license/Byggarepop/Unwritten.svg)](https://github.com/Byggarepop/Unwritten/blob/main/LICENSE)

**Catch the files you — or your AI agent — forgot to change.** Unwritten learns
from your git history which files usually change together and warns when one is
missing from your edit: "you changed `OrderService.cs` but not
`OrderServiceTests.cs`, and they change together 94% of the time." It calls
these missing companions *holes*, and every warning comes with its confidence
score and real example commits as proof.

```text
1 possible hole(s):

  src/Orders/OrderServiceTests.cs
    expected because you changed src/Orders/OrderService.cs
    confidence 0.826 (90 co-changes in 100 changes)
    e.g. 3f2a1c9 Add surcharge handling to freight calculation

FAIL: at least one hole at confidence >= 0.70.
```

It runs as an **MCP server** so AI coding agents (Claude Code, Copilot) can check
their own edits mid-session, and as a **CLI** for pre-commit hooks.
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

## See it in 60 seconds

https://github.com/user-attachments/assets/REPLACE-ME-BY-DRAGGING-THE-VIDEO-INTO-THE-WEB-EDITOR

<!-- The bare URL above renders as an inline video player on GitHub and
     degrades to a plain clickable link on nuget.org (which cannot embed
     video). To set or refresh it: edit README.md on github.com, drag
     videos/Unwritten__Long_16x9.mp4 into the editor at this spot, delete
     the placeholder URL, and commit. -->

## See it in action

`stats` shows what the tool has learned — here, one file pair coupled strongly
enough (confidence ≥ 0.7) to block a commit if one side is missing:

![Index stats showing one high-confidence file pair](https://raw.githubusercontent.com/Byggarepop/Unwritten/main/img/demo/check-index.png)

After editing one file of that pair, `check` warns that its companion is
missing and spells out the three ways to resolve it — update the companion,
commit anyway, or mute the rule:

![check reporting a missing companion file with resolution options](https://raw.githubusercontent.com/Byggarepop/Unwritten/main/img/demo/check-stats.png)

With the pre-commit hook installed, the same check runs automatically on every
commit and blocks it while the companion is still missing:

![Pre-commit hook blocking a commit on a missing companion file](https://raw.githubusercontent.com/Byggarepop/Unwritten/main/img/demo/use-pre-commit-hook.png)

## Documentation

Everything else lives in **[docs/README.md](https://github.com/Byggarepop/Unwritten/blob/main/docs/README.md)**:

- [Background & the research behind it](https://github.com/Byggarepop/Unwritten/blob/main/docs/README.md#this-is-not-a-new-idea--and-thats-the-point)
- [Why these thresholds (tested on real data)](https://github.com/Byggarepop/Unwritten/blob/main/docs/README.md#why-these-thresholds-tested-on-real-data)
- [Use: MCP server, CLI, hooks, muting false rules](https://github.com/Byggarepop/Unwritten/blob/main/docs/README.md#use)
- [Configuration reference](https://github.com/Byggarepop/Unwritten/blob/main/docs/README.md#configuration--unwrittenconfigjson)
- [How it works](https://github.com/Byggarepop/Unwritten/blob/main/docs/README.md#how-it-works)
- [What it does NOT do (yet)](https://github.com/Byggarepop/Unwritten/blob/main/docs/README.md#what-it-does-not-do-yet)

## License

[MIT](https://github.com/Byggarepop/Unwritten/blob/main/LICENSE)
