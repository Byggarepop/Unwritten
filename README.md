<!-- mcp-name: io.github.Byggarepop/conventionsense -->

# ConventionSense

**The free, agent-native slice of change coupling.** ConventionSense learns from your git
history which files are expected to change together, and flags statistically
confident *absences*: "you changed `OrderService.cs` but not
`OrderServiceTests.cs`, and they co-change 94% of the time."

It runs as an **MCP server** so AI coding agents (Claude Code, Copilot) can check
their own edits for holes mid-session, and as a **CLI** for pre-commit hooks.
One `dotnet tool execute`, an index in `.conventionsense/`, no server, no subscription,
no tokens.

## Quick start

From your repo's root:

```bash
# 1. Build the index from your git history
dotnet tool execute ConventionSense --yes -- reindex

# 2. Register as an MCP server (Claude Code)
claude mcp add conventionsense -- dotnet tool execute ConventionSense --yes -- mcp
```

That's it — your agent can now call `check_holes` after editing.

## This is not a new idea — and that's the point

ConventionSense modernizes a 20-year-old research lineage for the agent era. The field is
called *change coupling* (or *evolutionary coupling*), studied extensively by the
Mining Software Repositories community:

- **ROSE (Zimmermann, Weißgerber, Diehl & Zeller, 2004–2005)** mined association
  rules from version history in an Eclipse plugin — "programmers who changed
  these functions also changed…" — explicitly to prevent errors from incomplete
  changes, including warnings about missing items. ConventionSense is essentially ROSE
  reborn.
- **CodeScene** ships commercial change-coupling analysis, and its CI/CD delta
  analysis includes detecting the absence of expected coupling in PRs.

Twenty years of research prove the signal is real. What ConventionSense adds:

1. **Agent-native.** Coupling-absence checks exposed as MCP tools that coding
   agents call mid-edit. ROSE targeted humans in 2004-era IDEs; CodeScene's MCP
   server exposes Code Health analysis, not coupling-absence checks.
2. **Free, open-source, local, zero-infrastructure.** One dotnet tool, one JSON
   index in your repo, nothing else.
3. **Explicit calibration.** The confidence score is the Wilson lower bound of
   the co-change proportion — not the raw ratio, which overtrusts small samples —
   with an empirically validated alert floor (below).

## Why these thresholds (validated empirically)

A prototype was evaluated on EF Core (13,642 commits, 1,976 learned rules,
2,047 held-out test commits), measuring how often a flagged hole was "filled"
within 10 subsequent commits:

| Wilson-LB confidence | holes flagged | filled within 10 commits |
|---|---|---|
| 0.3–0.5 | 1,429 | ~10% (noise floor) |
| 0.6 | 254 | 50% |
| 0.7 | 328 | 66.5% |
| 0.8 | 133 | 79.7% |

Baked-in consequences (do not change without re-validating):

- **Wilson lower bound** (z = 1.96) is the confidence score.
- Default **alert floor 0.6** — the empirical phase transition; the CLI's
  default **fail floor is 0.7**.
- Commits touching **more than 30 files** are excluded from training
  (refactor/merge noise), as are **merge commits**.
- A rule needs **at least 10 changes** of the trigger file (minimum support).
- Every alert carries evidence — counts and example commit SHAs — because an
  absence points at nothing, so the alert must bring its own proof.

## Use

No install step — `dotnet tool execute` (or its alias `dnx`) downloads the tool
on first use and runs it:

```bash
dotnet tool execute ConventionSense --yes -- check --staged
# dnx ConventionSense --yes -- check --staged   works too
```

Everything before `--` is for the tool runner; everything after it is the
ConventionSense command line. (From source: `dotnet pack src/ConventionSense -o nupkg`,
then add `--source nupkg --prerelease` before the `--`.)

Add `.conventionsense/` to your `.gitignore` — the index is a local cache, rebuilt from
history on demand.

### As an MCP server (Claude Code example)

```bash
claude mcp add conventionsense -- dotnet tool execute ConventionSense --yes -- mcp
```

Or in any `.mcp.json`-style config:

```json
{
  "command": "dotnet",
  "args": ["tool", "execute", "ConventionSense", "--yes", "--", "mcp"]
}
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
dotnet tool execute ConventionSense --yes -- check --staged            # prints holes >= 0.6, exits 1 if any >= 0.7
dotnet tool execute ConventionSense --yes -- check --staged --fail-at 0.8   # stricter gate
dotnet tool execute ConventionSense --yes -- check src/Foo.cs src/Bar.cs    # check an explicit file set
```

The first call on a repository indexes its full history (seconds to a minute on
large repos); afterwards only new commits are ingested, and queries are pure
in-memory lookups.

### As a pre-commit hook

Plain git hook — create `.git/hooks/pre-commit` (no extension) with:

```sh
#!/bin/sh
exec dotnet tool execute ConventionSense --yes -- check --staged
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
      - id: conventionsense
        name: conventionsense hole check
        entry: dotnet tool execute ConventionSense --yes -- check --staged
        language: system
        pass_filenames: false
```

With [Husky](https://typicode.github.io/husky/): `echo "dotnet tool execute ConventionSense --yes -- check --staged" > .husky/pre-commit`.

### Configuration — `.conventionsense/config.json`

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
they take effect on the next full rebuild — run `dotnet tool execute ConventionSense --yes -- reindex` after changing
them. Command-line flags override the config file.

## How it works

For every non-merge commit touching ≤ 30 files, ConventionSense counts each file's
changes and each file pair's co-changes. A rule A→B exists when A has changed at
least 10 times and `wilson_lb(co(A,B), count(A)) ≥ floor`. The index lives in
`.conventionsense/index.json`, keyed by the indexed HEAD; when HEAD moves, only the new
commits are counted.

Internally the engine is domain-neutral — it counts *entities* in
*transactions*, and git is just the first adapter mapping commits to
transactions and file paths to entity ids. This keeps the door open for
member-level entities (Roslyn) and non-git event streams.

### Content-aware exceptions for JSON files

For rules whose trigger is a JSON file, ConventionSense additionally learns **which
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

With `"memberLevel": true`, ConventionSense builds a second index
(`.conventionsense/members.json`) where the entities are C# *members* —
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

## Development

```bash
dotnet test          # unit + integration tests (integration tests script synthetic git repos in temp dirs)
dotnet pack src/ConventionSense
```

Layout: `ConventionSense.Core` (pure counting/scoring engine — no git, no I/O),
`ConventionSense.Git` (thin adapter shelling out to `git`), `ConventionSense.Storage`
(`.conventionsense/index.json` persistence + incremental updates), `ConventionSense` (the
`conventionsense` tool: MCP server + CLI).
