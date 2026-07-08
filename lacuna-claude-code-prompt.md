# Prompt för Claude Code — kopiera allt nedanför linjen

---

Build a .NET MCP server called **Lacuna** — a git-history-based "hole detector" that learns which files are expected to change together, and flags statistically confident *absences* ("you changed A but not B, and they co-change 94% of the time"). Primary consumers are AI coding agents (Claude Code, Copilot) via MCP, plus a CLI mode for pre-commit hooks.

## Positioning & prior art (be honest about this in the README)

Lacuna is NOT a novel idea — it modernizes a 20-year-old research lineage for the agent era. The README must openly acknowledge:

- **ROSE (Zimmermann et al., 2004–2005)**: mined association rules from version history in an Eclipse plugin ("programmers who changed these functions also changed..."), explicitly to prevent errors from incomplete changes, including warnings about missing items. The research field is called *change coupling* / *evolutionary coupling* (MSR community).
- **CodeScene**: commercial product whose CI/CD delta analysis includes detecting the absence of expected change coupling in PRs.

What Lacuna adds that neither offers: (1) **agent-native** — change-coupling absence checks exposed as MCP tools that coding agents (Claude Code, Copilot) call mid-edit; ROSE targeted humans in 2004-era IDEs, and CodeScene's MCP server exposes Code Health analysis, not coupling-absence checks; (2) **free, open-source, local, zero-infrastructure** — one `dotnet tool install`, index in `.lacuna/`, no server, no subscription, no tokens; (3) **explicit calibration** — Wilson lower bound instead of raw confidence, with an empirically validated alert floor (see below).

Tagline direction: "the free, agent-native slice of change coupling" / "ROSE reborn for the agent era". This framing goes in the README's opening — acknowledging the lineage builds credibility; 20 years of research proves the signal is real.

## Why this design (validated empirically — do not change these numbers without re-validating)

A Python prototype was tested on EF Core (13,642 commits, 1,976 learned rules, 2,047 held-out test commits):

| Wilson-LB confidence | holes flagged | filled within 10 commits |
|---|---|---|
| 0.3–0.5 | 1,429 | ~10% (noise floor) |
| 0.6 | 254 | 50% |
| 0.7 | 328 | 66.5% |
| 0.8 | 133 | 79.7% |

Conclusions baked into the spec: (1) Wilson lower bound on the co-change proportion is the confidence score — raw ratios overtrust small samples; (2) default alert floor is **0.6** (the empirical phase transition); (3) commits touching **>30 files** are excluded from training (refactor/merge noise); (4) merge commits excluded; (5) every alert must carry evidence (counts + example commit SHAs) — an absence points at nothing, so the alert must bring its own proof.

## Stack

- .NET 10, C#, official `ModelContextProtocol` NuGet SDK, stdio transport (same integration pattern as a typical local MCP server).
- Git access: shell out to `git` (`log --name-only --no-merges --pretty=format:...`) — do NOT use libgit2sharp (native binary friction).
- Index persistence: single JSON file at `.lacuna/index.json` in the repo root (add to `.gitignore` advice in README). Store: indexed HEAD SHA, per-entity change counts, pair co-change counts, config.
- **Schema neutrality (deliberate design decision):** the index schema and core counting/scoring types must use domain-neutral naming — `entity` (string id), `transaction` (a set of entities that changed together), `EntityStats`, `PairStats` — NOT `filePath`/`commit`. Git is just the first *adapter*: one component maps commits→transactions and file paths→entity ids. The core (counting, Wilson scoring, hole lookup) must never reference git concepts. Rationale: phase 4+ may add Roslyn-based member-level entities ("method X in file Y") and the same engine should support non-git domains (any event stream with transactions). Keep the git adapter thin and the core pure.
- Incremental updates: on any tool call, if repo HEAD != indexed HEAD, index only the new commits (append counts), don't rebuild.

## MCP tools (phase 1 = tools 1–3)

1. `check_holes(files: string[], repoPath: string, minConfidence: double = 0.6)`
   For each input file A, find rules A→B where B is not in the input set. Return JSON: `{ hole: B, trigger: A, confidence, coChanges, totalChanges, exampleCommits: [3 SHAs + subjects] }`. This is the tool an agent calls after editing, passing the files it touched (or `--staged` in CLI mode).
2. `expected_companions(file: string, repoPath: string, top: int = 10)`
   Ranked co-change companions for a file, regardless of threshold — for proactive context injection when an agent opens a file.
3. `explain_rule(fileA: string, fileB: string, repoPath: string)`
   Full evidence: counts, confidence, up to 10 historical commits where they co-changed, and the commits where A changed alone (the exceptions — useful for judging whether the current case is an exception).
4. `reindex(repoPath: string)` — force full rebuild.
5. `stats(repoPath: string)` — index size, rule count at floors 0.5/0.6/0.7/0.8, indexed HEAD.

## CLI mode (falls out of the same core)

`lacuna check --staged` → prints holes ≥0.6, exit code 1 if any ≥0.7 (configurable). For use as pre-commit hook.

## Core algorithm (port faithfully)

```
wilson_lb(pos, n, z=1.96):
  p = pos/n
  return (p + z²/2n − z·sqrt(p(1−p)/n + z²/4n²)) / (1 + z²/n)
```

Training: for each commit (no merges, ≤30 files): for every ordered pair (A,B) in the commit, count[A]++ (once per file) and co[A,B]++. Rule A→B exists if count[A] ≥ 10 and wilson_lb(co[A,B], count[A]) ≥ floor. Handle file renames pragmatically: use paths as-is in phase 1; note rename-following as a known limitation in README.

## Quality bar

- xUnit tests. Build test fixtures as **synthetic git repos created in temp dirs** (script commits programmatically) so co-change counts are deterministic. Follow the convention: prefer polling assertions with timeouts over bare asserts where timing is involved.
- Wilson function: unit-test against known values (e.g. wilson_lb(15,17) ≈ 0.66, wilson_lb(21,22) ≈ 0.78).
- Query path must be pure lookup on the loaded index — target <50 ms, no git calls unless HEAD moved.
- Honest README: state what it does NOT do (no content-level exceptions yet, no rename tracking, "filled later" validation caveats), and include the prior-art section (ROSE, change coupling research, CodeScene) with the differentiation described under "Positioning & prior art".

## Phases — stop after each for review

- **Phase 1:** indexer + persistence + incremental update + tools 1–3 + CLI check. Prove it on this repo's own history.
- **Phase 2:** tools 4–5, config file (`.lacuna/config.json`: floors, support, mega-commit limit), pre-commit hook docs.
- **Phase 3 (design doc first, then implement):** content-aware exception layer for structured files — for a rule A→B where A is JSON, learn which changed *keys* in A predict co-change (same Wilson scoring per key) and suppress alerts when only non-predictive keys changed. Validated on a real case: a `server.json` description-only edit was correctly suppressed (key-level confidence 0.30 vs file-level 0.66) while all 21 genuine version-bump commits still triggered.

Start with Phase 1. Before writing code, present the project structure and the index JSON schema for approval.
