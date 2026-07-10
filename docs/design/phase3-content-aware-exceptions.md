# Phase 3 design: content-aware exception layer for structured files

Status: **implemented** (2026-07-05). Review decisions: suppressed holes are
excluded from the CLI exit code (`--strict` re-includes), `facetCandidateFloor`
= 0.5, backfill runs inline on the first update after a rule crosses the floor.

## Problem

File-level rules are blind to *what* changed inside the trigger file. The rule
"`server.json` → `manifest.json` (0.66)" fires on a description-typo edit just
as loudly as on a version bump, even though history shows description-only
edits never required the companion change. False alarms erode trust fastest
exactly where coupling is strongest — busy structured files (configs,
manifests, lockfile-adjacent JSON) that get many small unrelated edits.

The empirical motivation (from the Python prototype, spec §Phases): on a real
`server.json` → companion rule, key-level scoring suppressed a
description-only edit (key confidence 0.30 vs file-level 0.66) while all 21
genuine version-bump commits still triggered.

## Core idea

For a rule A→B where A is a structured file (JSON in this phase), learn
**which changed keys in A predict the co-change** — with the exact same Wilson
machinery, one level down:

- File level (existing): `wilson_lb(co(A,B), count(A))`
- Key level (new): for each key path `k` of A:
  `wilson_lb(co_k(A,B), count_k(A))`, where `count_k` = commits in which A
  changed *and key k changed*, and `co_k` = those commits where B also changed.

At query time, when a hole A→B would be reported and we know which keys the
current edit touched: if **every** touched key is *known non-predictive*, the
alert is suppressed (reported with `suppressed: true`, see below). If any
touched key is predictive, unknown, or the key diff cannot be computed — the
alert stands. **Fail open, always**: this layer can only ever downgrade an
alert with positive evidence, never invent silence from missing data.

## Domain model (keeping the core pure)

The core gains one neutral concept: a **facet** — a named sub-division of an
entity (for JSON files: a canonical key path). Transactions may optionally
carry, per entity, the set of facets that changed. The core never knows what a
facet *is*; the JSON adapter produces them, the same way the git adapter
produces entities.

```
Transaction = (Id, Label, Entities, FacetChanges?: entity → set<facet>)
FacetPairStats: for (trigger entity A, companion B, facet k): countK, coK
```

Facet naming for JSON (adapter concern):

- Canonical path segments joined by `.`: `mcpServers.args`, `info.version`
- Array indices collapsed to `[]`: `packages[].version` — index positions are
  noise; the *shape* of the change is the signal
- Depth capped at **3** segments (deeper changes roll up to their depth-3
  ancestor); at most **64 distinct facets tracked per entity** (beyond that,
  the file is facet-untrackable — fail open). Both bounds configurable.
- A change in key `k` = leaf value added/removed/modified anywhere under `k`
  (computed by structural diff of parsed JSON, not text diff).

## Training: how facet stats get built

Facet extraction needs historical file *content* (`git show sha:path` for the
commit and its parent) — two blob reads per (JSON file × commit). Doing that
for every JSON file in history would be expensive and pointless. So:

1. **Only rule participants get facet stats.** After (re)building file-level
   counts, select trigger files A that are (a) JSON by extension (`.json`),
   (b) trigger of at least one pair with file-level Wilson ≥ `facetCandidateFloor`
   (default **0.5** — slightly below the alert floor so rules hovering near
   0.6 get coverage too).
2. For each selected A: walk its commit history (`git log --format=%H -- A`,
   same exclusions: no merges, ≤30 files), structurally diff A between parent
   and commit, record changed facets, and bump `countK` / `coK(B)` for each
   companion B of A's qualifying pairs.
3. Unparseable versions of the file (invalid JSON at that commit, file added
   — no parent version, binary) contribute nothing: the commit is skipped for
   facet purposes (it still counts at file level as before).
4. **Incremental updates** extend facet stats commit-by-commit like file
   stats. A *new* rule crossing `facetCandidateFloor` for the first time
   triggers a one-off backfill walk of its trigger's history on that update.

Storage (index.json additions, schema-neutral naming, version stays 1 with an
optional section — old indexes load fine, missing section = no suppression):

```jsonc
"facets": {
  "server.json": {                    // trigger entity
    "manifest.json": {                // companion entity
      "version":     { "count": 21, "co": 21 },
      "description": { "count": 10, "co": 3 }
    }
  }
}
```

Size bound: (#JSON rule triggers) × (#their companions) × (≤64 facets) — in
practice tens of entries, negligible next to `pairs`.

## Query time: suppression

`check_holes` (and CLI `check`) gains one step. For each hole A→B that would
be reported, where A is JSON and `facets[A][B]` exists:

1. Compute the **currently changed facets** of A: structural diff of A's
   working-tree content (CLI `--staged`: the staged blob) against `HEAD:A`.
   This is a git call on the query path — same class of documented exception
   as `explain_rule`'s: it runs only when a hole is about to fire on a JSON
   trigger with facet data, i.e. rarely, and never on the common
   nothing-to-report path.
2. For each changed facet k, classify:
   - **predictive**: `count_k ≥ minFacetSupport` and `wilson_lb(co_k, count_k) ≥ facetFloor`
   - **non-predictive**: `count_k ≥ minFacetSupport` and wilson below `facetFloor`
   - **unknown**: `count_k < minFacetSupport`, or facet never seen
3. Suppress **only if** the changed-facet set is non-empty and consists
   entirely of non-predictive facets. Any predictive or unknown facet, an
   empty diff, unparseable content, or any error → alert stands.

Suppressed holes are not deleted — they are returned with:

```jsonc
{ "hole": "...", "suppressed": true,
  "suppressReason": "all changed keys are historically non-predictive",
  "changedFacets": [ { "facet": "description", "confidence": 0.30, "coChanges": 3, "totalChanges": 10 } ] }
```

MCP consumers (agents) see the reasoning and can overrule it. The CLI prints
suppressed holes as one dim summary line each and **excludes them from the
exit-code decision** — that is the actual false-alarm relief.

`explain_rule` gains a `facetBreakdown` section (per-facet counts and
confidence) when facet stats exist for the pair — so "why was this
suppressed?" has a first-class answer.

## Config additions (`.unwritten/config.json`)

| Key | Default | Meaning |
|---|---|---|
| `facetFloor` | 0.6 | Wilson floor for a facet to count as predictive (same scale as file floor) |
| `minFacetSupport` | 5 | Min changes of a facet before it can be classified (below → unknown → no suppression) |
| `facetCandidateFloor` | 0.5 | File-level confidence from which a JSON trigger gets facet training |
| `maxFacetsPerEntity` | 64 | Beyond this, the file is facet-untrackable (fail open) |
| `facetMaxDepth` | 3 | Key-path depth cap |

`minFacetSupport` is deliberately lower than file-level `minSupport` (10): the
sample is conditioned on "A changed", so n is inherently smaller, and the cost
of a wrong classification is bounded (worst case: one alert more or one alert
fewer, never a new false rule).

## What this phase does NOT attempt

- **Only JSON.** YAML/TOML/XML share the same facet model and can be added as
  parsers later; source code (Roslyn member-level) is the phase-4+ item and
  needs symbol identity, not just paths.
- **No cross-file facet rules** (key k in A → key m in B). Companion side
  stays file-level; the evidence table stays 2-dimensional.
- **No semantic classification** of keys ("description fields are cosmetic").
  Everything is learned from history; a description key that historically
  predicts co-change will be treated as predictive — correctly.
- **Facet evidence commits are not stored** (only counts). `explain_rule`
  can already list co-change/alone commits; per-facet example SHAs would
  triple the storage for marginal value. Revisit if users ask "which commits
  changed only this key?".

## Validation plan

1. **Unit (core)**: facet stats counting and the suppression classifier on
   hand-built transactions with facet sets — including the fail-open matrix
   (unknown facet, empty diff, missing stats, parse failure).
2. **Unit (JSON adapter)**: structural diff — nested changes, array collapse,
   depth cap, add/remove vs modify, invalid JSON, duplicate keys.
3. **Integration (synthetic repo)**: replicate the prototype's validated case
   shape: `a.json` where facet `version` co-changes with `b.txt` 12×, facet
   `description` changes alone 8× with 2 co-changes. Assert: description-only
   staged edit → hole suppressed, exit 0; version edit → hole fires, exit 1;
   edit touching a brand-new key → fires (unknown = fail open); incremental
   update extends facet counts.
4. **Real-repo spot check**: the csharp-sdk clone has JSON files but its only
   strong rule is a `.cs` pair, so expected outcome there is "no behavior
   change" — which is itself the test: the layer must be invisible when it has
   nothing to say. For a positive real-world case we'd want a repo with a
   coupled manifest pair (e.g. an MCP servers registry); optional, can be done
   at review time.

## Open questions for review

1. **Suppressed holes in CLI exit code** — proposed: excluded (that's the
   point), with `--strict` flag to re-include them. Agree?
2. **`facetCandidateFloor` at 0.5** costs extra training git-show calls for
   near-miss rules; set it equal to the alert floor (0.6) to train less?
3. **Backfill on incremental update** (new rule crosses the floor → walk its
   trigger's full history once) can make one `check_holes` call slow on a
   just-crossed rule. Acceptable, or should backfill only happen on `reindex`?
