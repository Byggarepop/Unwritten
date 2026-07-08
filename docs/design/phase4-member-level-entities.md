# Phase 4 design: member-level entities (C# via Roslyn)

Status: **implemented** (2026-07-05). Review decisions: member indexing
opt-in; history window 5,000 commits (configurable via `memberHistoryWindow`);
member ids auto-detected in tool params (lookup-based); C# cosmetic-edit
suppression of file-level holes included.

Perf validation (full clone of modelcontextprotocol/csharp-sdk, 728 commits):
first build 17 s total (file + member index), 335 member transactions after
mega-commit exclusion, 3,142 members, 43,699 pairs, 6.3 MB members.json.
Finding: only 2 members reached the support threshold of 10 — member rules
need older/hotter codebases than this one; `memberMinSupport` may deserve
empirical re-validation (EF Core-style) before lowering.

## Problem

File-level rules answer "you changed `Foo.cs`, where is `FooTests.cs`?" but a
file is a blunt unit: `Foo.cs` may hold twenty members with different coupling
behavior. The interesting question is one level down: *"you changed
`Foo.Bar()`; history says `FooTests.Bar_works()` co-changes 9 times out of 10
— and it is absent from your edit."*

Unlike phase 3 (which only *silences* file-level alerts), phase 4 *detects*
holes at member granularity: members become first-class entities with their own
counts, pairs, and Wilson-scored rules. This is why the core was built
schema-neutral — the engine needs zero changes; everything below is adapter,
storage, and surface work.

## Design decisions

### D1. Members are entities in a separate index — not facets, not mixed in

- **Not facets**: facets only refine the trigger side; they cannot produce
  member→member rules.
- **Not mixed into the file index**: file and member entities in one index
  would double-alert every hole (file pair + its member pairs) and multiply
  pair counts. Two indexes, same engine, same schema:
  `.conventionsense/index.json` (files, unchanged) and `.conventionsense/members.json`
  (members, `source.adapter: "git+roslyn"`).
- Queries run against both and report `fileHoles` and `memberHoles` as
  separate sections. A member hole and its enclosing file hole may both appear;
  consumers see the member one as the more actionable of the two.

### D2. Member identity: fully-qualified name + arity, NO file path

`Namespace.Type.Member/arity` (e.g. `MyApp.Orders.OrderService.Submit/2`,
nested types with `+`). Rationale:

- Excluding the file path makes identity survive file renames and moves —
  the thing file-level ConventionSense explicitly cannot do. Partial classes collapse
  to the same entity, which is semantically correct.
- **Arity, not full signature**: parameter *renames* and type-name churn
  don't break identity; overloads with different parameter counts stay
  distinct. Same-arity overloads collapse to one entity — accepted noise,
  far cheaper than signature normalization.
- Member *renames* break identity, exactly like file renames in phase 1.
  Same documented limitation, same pragmatic stance.
- Covered member kinds: methods, constructors, properties, events, fields
  (arity 0), operators. Changes outside any member (class attributes, base
  list, usings) roll up to the type entity `Namespace.Type`.
- Local functions and lambdas roll up to their containing member.
- For display, the index keeps a `memberLocations` map (entity id → last
  seen file path) so tools can print clickable locations.

### D3. "Member changed" = normalized syntax differs (Roslyn, syntax-only)

Per commit, per changed `.cs` file: parse before/after content with
`Microsoft.CodeAnalysis.CSharp` (syntax trees only — no compilation, no
semantic model; fully-qualified names are derived from the syntactic
namespace/type nesting). A member changed when its **trivia-stripped**
(whitespace + comments removed) span differs, or it was added/removed.

Free byproduct worth having: a comment-only or formatting-only edit changes
*no* member — so member-level rules are naturally immune to cosmetic edits,
the C# analog of phase 3's key suppression, without any extra machinery.

Excluded from parsing: generated code (`*.g.cs`, `*.generated.cs`,
`*.Designer.cs`, anything under `obj/`), configurable glob. Files that fail
to parse at either end of a diff contribute nothing for that commit (fail
open, as always).

### D4. Cost control — this is the expensive phase

Member training needs 2 blob reads + 2 parses per (changed .cs file ×
commit). On EF Core-scale history that's hundreds of thousands of parses. So:

1. **Opt-in.** Member indexing is off by default; enabled by
   `"memberLevel": true` in `.conventionsense/config.json` (or `conventionsense reindex
   --members` once). File-level behavior never changes for repos that don't
   opt in.
2. **History window.** Member training covers the most recent
   `memberHistoryWindow` commits (default **5,000**). Recency is a feature,
   not just a cost cap: member coupling drifts faster than file coupling.
3. **Blob reads via `git cat-file --batch`** (one long-lived process fed a
   list of `sha:path` requests) instead of one `git show` process per blob —
   two orders of magnitude fewer process spawns. This becomes a new
   `GitRunner` capability with the same stdin-redirect discipline as before.
4. **Member transaction cap.** A commit contributing more than
   `memberMaxTransactionSize` changed members (default **50**) is excluded
   from member training — the member-level analog of the 30-file mega-commit
   rule, same philosophy: refactor sweeps teach noise.
5. Incremental updates work as at file level; the window slides (old commits
   are *not* evicted retroactively — counts only ever grow — the window
   bounds the initial build and backfills).

Rough cost estimate to state in the README: initial member build ≈ parse time
of every changed-file version in the window; on a mid-size repo (5k commits)
minutes, not seconds. Warm queries stay pure lookups, unchanged.

### D5. Query surface

- `check_holes(files, repoPath, ...)` — unchanged signature. When a member
  index exists: for each input `.cs` file, diff working tree (or staged)
  against HEAD, extract its changed members, and run member-level
  `FindHoles` with those as the input set (a member of an input file that
  didn't change is NOT part of the input set — that's the added precision).
  Response gains `memberHoles: [{ hole, holeLocation, trigger, confidence,
  coChanges, totalChanges, exampleCommits }]`.
- `expected_companions` / `explain_rule` — accept member ids in the existing
  string params (detected by shape: contains `/` arity suffix and no path
  separator, or explicit `member:` prefix — reviewer input welcome, see Q3).
- `stats` — gains a `memberIndex` section (window, member count, pair count,
  rules at floors).
- CLI `conventionsense check` prints member holes under each file hole; member holes
  participate in the exit-code decision with the same `failConfidence`.
- Separate thresholds: `memberMinSupport` (default **10**, same as files
  until measured otherwise) — member histories are sparser, so this may
  need lowering after real-world validation; keeping it equal avoids
  introducing unvalidated constants.

### D6. Failure modes (fail-open matrix)

| Situation | Behavior |
|---|---|
| File unparseable at either end of a diff | No member observations for that commit; file-level unaffected |
| Repo not opted in / no members.json | Member sections absent, everything else as phase 3 |
| Blobless/partial clone (blob fetch slow or offline) | Training may be slow or fail per-blob → those commits skipped; never blocks file-level indexing |
| Same-arity overloads | Collapse to one entity (accepted) |
| Member renamed | New entity starts from zero (documented limitation) |
| >50 members changed in a commit | Commit excluded from member training |

## Implementation stages (each independently testable)

1. **Syntactic differ + identity scheme** (`ConventionSense.Roslyn` project):
   `MemberDiff.ChangedMembers(beforeSource, afterSource) → set of member ids`
   plus the id builder. Pure functions, heavy unit-test matrix (nesting,
   partial classes, overloads, namespaces incl. file-scoped, comment-only
   edits, top-level statements, parse errors).
2. **Batch blob reader**: `git cat-file --batch` wrapper in `ConventionSense.Git` +
   tests against synthetic repos.
3. **Member training + storage**: second `CoChangeIndex` built through the
   window, persisted to `members.json` (reusing `IndexStore` generically),
   `memberLocations`, incremental updates. Integration tests on synthetic
   repos (member rules emerge; comment-only commits teach nothing).
4. **Query integration**: tools + CLI + stats, member-id detection,
   working-tree member diff at query time. End-to-end test: edit method m1
   only → member hole for its test method; edit only a comment → no member
   hole (file hole may still fire).
5. **Perf validation on a real full clone** (not the blobless one): measure
   build time and index size, tune defaults, record numbers in the README the
   way phase 1 recorded the EF Core table.

Dependency added: `Microsoft.CodeAnalysis.CSharp` (syntax only — no
workspaces/MSBuild packages).

## Open questions for review

1. **Opt-in default** — proposed off-by-default (`memberLevel: true` to
   enable). Alternative: auto-enable when the repo is majority-C# and history
   is under some size. Preference?
2. **History window** — 5,000 commits proposed. Bigger default (more
   evidence) vs faster first build?
3. **Member id syntax in tool params** — auto-detect by shape vs explicit
   `member:` prefix on `expected_companions`/`explain_rule` inputs. Prefix is
   uglier but unambiguous.
4. **C# comment-only edits and FILE-level rules** — the differ can also tell
   us "this .cs edit changed no member", which could suppress *file-level*
   holes for cosmetic edits (phase-3 style, `suppressed: true`). Free to add,
   but it widens the phase. Include, or defer to phase 5?
