<!-- mcp-name: io.github.Byggarepop/unwritten -->

![Unwritten logo](https://raw.githubusercontent.com/Byggarepop/Unwritten/main/img/unwritten-steps-128.png)

# Unwritten

[![NuGet](https://img.shields.io/nuget/v/Unwritten.svg)](https://www.nuget.org/packages/Unwritten)
[![Downloads](https://img.shields.io/nuget/dt/Unwritten.svg)](https://www.nuget.org/packages/Unwritten)
[![License: MIT](https://img.shields.io/github/license/Byggarepop/Unwritten.svg)](LICENSE)

**The free, agent-native slice of change coupling.** Unwritten learns from your git
history which files are expected to change together, and flags statistically
confident *absences*: "you changed `OrderService.cs` but not
`OrderServiceTests.cs`, and they co-change 94% of the time."

It runs as an **MCP server** so AI coding agents (Claude Code, Copilot) can check
their own edits for holes mid-session, and as a **CLI** for pre-commit hooks.
One `dotnet tool execute`, an index in `.unwritten/`, no server, no subscription,
no tokens.

## Quick start

From your repo's root:

```bash
# 1. Build the index from your git history
dotnet tool execute Unwritten --yes -- reindex

# 2. Register as an MCP server (Claude Code)
claude mcp add unwritten -- dotnet tool execute Unwritten --yes -- mcp
```

That's it — your agent can now call `check_holes` after editing.

## This is not a new idea — and that's the point

Unwritten modernizes a 20-year-old research lineage for the agent era. The field is
called *change coupling* (or *evolutionary coupling*), studied extensively by the
Mining Software Repositories community:

- **ROSE (Zimmermann, Weißgerber, Diehl & Zeller, 2004–2005)** mined association
  rules from version history in an Eclipse plugin — "programmers who changed
  these functions also changed…" — explicitly to prevent errors from incomplete
  changes, including warnings about missing items. Unwritten is essentially ROSE
  reborn.
- **CodeScene** (commercial) is the closest neighbor. Its delta analysis can
  also warn when an expected change pattern is broken — a file that usually
  changes with another is missing from a pull request. The difference is
  *where* and *how* it runs: CodeScene is a server product that analyzes pull
  requests after the fact, as part of a paid platform for code health,
  hotspots, and team analytics. Unwritten is one small free tool that answers
  the same kind of question *while the code is being written* — inside the
  coding agent's loop, before anything is committed — locally, with no server,
  and with every warning carrying its explicit confidence score and example
  commits as proof.

**What Unwritten adds:**

**Built for AI assistants.** Coding agents like Claude Code and Copilot can ask Unwritten "did I forget anything?" *while they work* — not after the pull request is opened. Older tools were built for humans clicking in an editor (ROSE, 2004) or review changes server-side after the fact (CodeScene).

**Free and simple.** Open source, runs on your machine, needs no server or account. One installed tool, one small index file in your repo — that's it.

**Honest about certainty.** If two files changed together 3 times out of 4, that could easily be coincidence. If it happened 90 times out of 100, it's a real pattern. Unwritten uses a statistical formula (the Wilson lower bound) that scores evidence based on both how often the pattern held *and* how much evidence there is:

- 3 out of 4 → raw ratio 75%, but confidence only **0.30** — too little evidence to trust
- 15 out of 17 → raw ratio 88%, confidence **0.66** — starting to look real
- 90 out of 100 → raw ratio 90%, confidence **0.82** — a real pattern
- 15 out of 15 → a perfect record, confidence **0.80** — a small project *can* earn high confidence, but only with a spotless history

So more evidence is always worth more: 3 out of 4 and 90 out of 100 get very different scores, even though the percentages look similar. And when two patterns do end up with the same score, they've earned it — a small project needs a near-perfect record to reach what a large one reaches with a few exceptions.

## Why these thresholds (tested on real data)

We tested the idea on EF Core, a large Microsoft project (13,642 commits). The tool learned 1,976 patterns from the older history, then flagged "holes" — expected files that were missing from a change — in the 2,047 newest commits. We then checked: was the missing file actually changed soon after (within 10 commits)? If yes, the warning was probably right.

| Confidence | holes flagged | turned out to be right |
|---|---|---|
| 0.3–0.5 | 1,429 | ~10% (basically noise) |
| 0.6 | 254 | 50% |
| 0.7 | 328 | 66.5% |
| 0.8 | 133 | 79.7% |

The pattern is clear: below 0.6 the warnings are mostly noise, above 0.6 they quickly become trustworthy. That's why these defaults are built in (don't change them without re-testing):

- Warnings start at confidence **0.6** — where the data shows warnings start being right. The CLI blocks a commit only at **0.7**.
- Commits touching more than 30 files are ignored during learning — big refactors and merges would teach false patterns.
- A file must have changed at least 10 times before rules about it are trusted.
- **Every warning comes with proof** — how many times the pattern held, and links to real example commits. A warning about something *missing* can't point at a file, so it has to bring its own evidence.

## Use

No install step — `dotnet tool execute` (or its alias `dnx`) downloads the tool
on first use and runs it:

```bash
dotnet tool execute Unwritten --yes -- check --staged
# dnx Unwritten --yes -- check --staged   works too
```

Everything before `--` is for the tool runner; everything after it is the
Unwritten command line.

Add `.unwritten/` to your `.gitignore` — the index is a local cache, rebuilt from
history on demand.

### As an MCP server

**Claude Code:**

```bash
claude mcp add unwritten -- dotnet tool execute Unwritten --yes -- mcp
```

**VS Code (Copilot Chat):** create or edit `.vscode/mcp.json` in your workspace:

```json
{
  "servers": {
    "unwritten": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["tool", "execute", "Unwritten", "--yes", "--", "mcp"]
    }
  }
}
```

**Visual Studio 2026 (GitHub Copilot Chat):** the same `servers` snippet as VS Code,
in `%USERPROFILE%\.mcp.json` (all solutions) or a `.mcp.json` next to your solution
file — then restart Visual Studio so it loads the server.

Any other stdio MCP client: `dotnet` with
`["tool", "execute", "Unwritten", "--yes", "--", "mcp"]`.

**Through an MCP gateway** (e.g. [McpOrchestrator](https://github.com/Byggarepop/dotnet-mcp-orchestrator)):
the router's LLM only sees your capability description, so give it one that says
*when* to call Unwritten. Copy this as the capability's instructions:

```text
Unwritten — repo convention guard. Learns which files AND
code members (methods, classes) historically change together
(mined from git history, statistical confidence with evidence)
and flags expected-but-missing companion changes at both levels.

CALL THIS WHEN:
- You have made ANY code change in a git repo — modified a method,
  added a class, renamed something, edited config, created or
  deleted files — and are about to finish a task, commit, or hand
  back to the user → check_holes with the changed files. Works at
  member level too: "you changed CalculateFreight but not its
  tests" — so call it even when only method bodies changed.
- You are planning a change and want to know which files or
  members usually accompany the one you're about to touch →
  expected_companions.
- A hole was flagged and you need to judge whether this case is a
  legitimate exception → explain_rule (returns historical evidence
  and past exceptions).
- The user asks "did I/you forget anything?", "what usually
  changes with this?", or mentions co-change/conventions →
  check_holes or expected_companions.
- Index maintenance: reindex after large history changes; stats
  for index health.

DO NOT CALL FOR: code quality/lint/style questions, test
execution, non-git workspaces, or questions about code *content*.
Warnings are patterns, not rules — high-confidence holes (≥0.7)
should be fixed or explicitly justified; lower ones are
suggestions.
```

Tools exposed:

| Tool | Purpose |
|---|---|
| `check_holes(files, repoPath, minConfidence=0.6)` | After editing, pass the files you touched; returns expected-but-absent files with evidence. |
| `expected_companions(file, repoPath, top=10)` | Ranked co-change companions of a file, regardless of threshold — proactive context when opening a file. |
| `explain_rule(fileA, fileB, repoPath)` | Full evidence for one rule: counts, confidence both directions, co-change commits, and recent commits where A changed alone (the exceptions). |
| `stats(repoPath)` | Index health: indexed HEAD, transaction/entity/pair counts, index size, rule counts at floors 0.5/0.6/0.7/0.8. |
| `reindex(repoPath)` | Force a full rebuild — after history rewrites or config changes. |

### As a CLI

```bash
dotnet tool execute Unwritten --yes -- check --staged            # prints holes >= 0.6, exits 1 if any >= 0.7
dotnet tool execute Unwritten --yes -- check --staged --fail-at 0.8   # stricter gate
dotnet tool execute Unwritten --yes -- check src/Foo.cs src/Bar.cs    # check an explicit file set
```

The first call on a repository indexes its full history (seconds to a minute on
large repos); afterwards only new commits are ingested, and queries are pure
in-memory lookups.

### As a pre-commit hook

Plain git hook — create `.git/hooks/pre-commit` (no extension) with:

```sh
#!/bin/sh
exec dotnet tool execute Unwritten --yes -- check --staged
```

and make it executable (`chmod +x .git/hooks/pre-commit`; not needed on
Windows). The commit is blocked when any hole reaches the fail floor (default
0.7); holes between 0.6 and 0.7 are printed as warnings but do not block. To
bypass once: `git commit --no-verify`.

With the [pre-commit](https://pre-commit.com) framework, add to
`.pre-commit-config.yaml`:

```yaml
repos:
  - repo: local
    hooks:
      - id: unwritten
        name: unwritten hole check
        entry: dotnet tool execute Unwritten --yes -- check --staged
        language: system
        pass_filenames: false
```

With [Husky](https://typicode.github.io/husky/): `echo "dotnet tool execute Unwritten --yes -- check --staged" > .husky/pre-commit`.

### Configuration — `.unwritten/config.json`

All settings are optional; missing ones use the validated defaults:

```jsonc
{
  "minSupport": 10,            // min changes of the trigger file before rules exist
  "maxTransactionSize": 30,    // commits touching more files are excluded from training
  "defaultMinConfidence": 0.6, // alert floor (check_holes + CLI report floor)
  "failConfidence": 0.7,       // CLI: exit 1 when a hole reaches this
  "maxExamplesPerPair": 10,    // evidence commits kept per file pair

  // Content-aware exceptions (JSON trigger files):
  "facetFloor": 0.6,           // Wilson floor for a key to count as predictive
  "minFacetSupport": 5,        // min changes of a key before it can be classified
  "facetCandidateFloor": 0.5,  // file-level confidence from which keys get trained
  "maxFacetsPerEntity": 64,    // beyond this the file is key-untrackable (fail open)
  "facetMaxDepth": 3,          // key-path depth cap (deeper rolls up)

  // Member-level indexing (C#/Roslyn), OFF by default:
  "memberLevel": false,        // enable member->member rules (first build costs seconds-to-minutes)
  "memberHistoryWindow": 5000, // member training covers this many most-recent commits
  "memberMinSupport": 10,      // min changes of a trigger member before member rules exist
  "memberMaxTransactionSize": 50 // commits changing more members are excluded (refactor noise)
}
```

Floors take effect immediately. Training settings (`minSupport`,
`maxTransactionSize`, `maxExamplesPerPair`) describe how the index is built, so
they take effect on the next full rebuild — run `dotnet tool execute Unwritten --yes -- reindex` after changing
them. Command-line flags override the config file.

## How it works

For every non-merge commit touching ≤ 30 files, Unwritten counts each file's
changes and each file pair's co-changes. A rule A→B exists when A has changed at
least 10 times and `wilson_lb(co(A,B), count(A)) ≥ floor`. The index lives in
`.unwritten/index.json`, keyed by the indexed HEAD; when HEAD moves, only the new
commits are counted.

Internally the engine is domain-neutral — it counts *entities* in
*transactions*, and git is just the first adapter mapping commits to
transactions and file paths to entity ids. This keeps the door open for
member-level entities (Roslyn) and non-git event streams.

### Content-aware exceptions for JSON files

For rules whose trigger is a JSON file, Unwritten additionally learns **which
keys** predict the co-change — the same Wilson scoring, one level down
(key paths like `info.version`, arrays collapsed to `packages[].name`).
When your current edit touched only keys that history says are
non-predictive (e.g. a description-only edit to a manifest whose `version`
key drives the coupling), the hole is *suppressed*: agents receive it with
`suppressed: true` plus the per-key evidence and may overrule; the CLI
prints one summary line and excludes it from the exit code (`--strict`
restores the old behavior).

The layer **fails open** in every direction: unknown keys, files that are
invalid JSON at either end of the diff, too many keys (>64), missing
history — all leave the alert untouched. Key training only runs for JSON
files that participate in rules, so index build cost is unchanged for
repos without JSON coupling. `explain_rule` exposes the per-key breakdown
(`facetBreakdown`) whenever it exists.

### Member-level rules for C# (opt-in)

With `"memberLevel": true`, Unwritten builds a second index
(`.unwritten/members.json`) where the entities are C# *members* —
`Namespace.Type.Method/arity` — extracted by diffing each commit's changed
`.cs` files syntactically (Roslyn, no compilation). `check_holes` then also
reports **memberHoles**: companion methods that history couples to the
members you actually changed, but which are absent from your edit. Member
ids deliberately exclude the file path, so member identity survives file
moves and renames. Because "changed" means *trivia-stripped syntax
differs*, comment- and whitespace-only edits change no member — which also
suppresses file-level holes for cosmetic C# edits (`suppressed: true`,
CLI `--strict` overrides), whether or not member indexing is enabled.

Costs and caveats: the first member build parses every changed-file
version in the history window (default: most recent 5,000 commits) —
seconds on small repos, minutes on large ones; incremental updates are
cheap. Generated code (`*.g.cs`, `*.Designer.cs`, `obj/`) is excluded.
Commits changing more than 50 members are excluded as refactor noise.
Member renames reset history, and same-arity overloads share one entity.
Member histories are much sparser than file histories: young repos will
often have **no** member rules at the default support of 10 — that is the
statistics being honest, not a bug. `expected_companions` and
`explain_rule` accept member ids directly (auto-detected).

## What it does NOT do (yet)

Honesty section — known limitations of phase 1:

- **No rename tracking.** Paths are taken as-is; a renamed file starts its
  history from zero. (`git log --follow` style rename-following is a known
  gap.)
- **Content-level exceptions cover JSON and C# only.** Other structured
  formats (YAML, TOML, XML) and other languages are treated at file level:
  any edit triggers the rule.
- **Member-level rules are C#-only, opt-in, and support-hungry.** Repos
  need members changed ≥10 times inside the history window before member
  rules exist; member renames and same-arity overloads blur identity.
- **"Filled later" validation caveat.** The empirical table above measures
  whether flagged holes were filled within 10 commits — a proxy for "the alert
  was right", not proof of causality. Some "unfilled" holes may still have been
  real omissions that were never fixed.
- **One git call in `explain_rule`.** Hot-path queries (`check_holes`,
  `expected_companions`) are pure index lookups; `explain_rule` additionally
  runs one targeted `git log -- <fileA>` to list commits where A changed alone.

