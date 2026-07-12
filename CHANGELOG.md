# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.4.0] - 2026-07-12

### Added

- **`unwritten ignore <trigger> <hole> --for <n>`** — bounded mute for a
  persistently false rule. Expires after the trigger has changed `n` more
  times (default 30; those are the commits that erode or re-confirm the rule),
  shows as `suppressed` with the remaining budget rather than disappearing,
  is overridden by `check --strict`, and lives in machine-managed
  `.unwritten/ignores.json`. `--list` / `--remove` manage entries; expired
  entries are pruned automatically. Deliberately CLI-only — no MCP tool, so
  agents cannot mute their own warnings. Permanent ignores do not exist by
  design: unbounded mutes go stale and eventually hide real omissions.

### Changed

- Refreshed project artwork (package icon, registry icons, social card).

## [0.3.0] - 2026-07-12

### Added

- **`baseRef` support** (MCP `check_holes` parameter, CLI `--base <rev>`): measure
  the edit against a chosen revision instead of HEAD. Essential for commit-as-you-go
  agent sessions — pass the pre-work SHA and committed edits are still seen.
- **Changed-file auto-detection**: omit `files` in `check_holes` (or run
  `unwritten check` with no arguments) to check every uncommitted change —
  staged, unstaged, and untracked (or everything since `baseRef`).
- **Per-file data status**: `check_holes` returns `checkedFiles` with
  `totalChanges`/`canTriggerRules` per input, plus `notes` when a file has no or
  too little history — an empty result now says "no data" instead of looking
  like "all good". The CLI prints the same as `note:` lines.
- **`unwritten install-hook`**: one command installs a git pre-commit hook
  (`--git`), a Claude Code Stop hook (`--claude-code`), or both — deterministic
  invocation instead of hoping the agent remembers the tool. The Stop hook runs
  `unwritten hook stop`, which feeds failing holes back to the agent (exit 2)
  and always fails open.
- **Automatic reindex on training-config change**: editing training settings in
  `.unwritten/config.json` (minSupport, maxTransactionSize, member settings
  including memberHistoryWindow, …) now triggers a rebuild on the next query;
  floor changes apply immediately, including in the MCP server. Manual
  `reindex` remains for history rewrites. Existing member indexes are rebuilt
  once (the history window is now part of the persisted training fingerprint).
- `.unwritten/` now writes its own `.gitignore` — no root .gitignore edit needed.
- First index build creates `.unwritten/config.json` as a fully commented
  template: every setting is discoverable in place, and nothing is pinned, so
  future default improvements still apply.
- Config validation with clear errors (invalid JSON or out-of-range values in
  `.unwritten/config.json` no longer crash with a stack trace).
- CLI: `--version`, `--help`/`-h` (also for `check`), friendly error for
  repositories without commits.

### Fixed

- **False "cosmetic edit" suppression after committing**: when the working tree
  matched HEAD (agent committed, then checked), every C#-triggered hole was
  suppressed as a whitespace/comment-only edit. Identical content is now treated
  as "no visible edit" and fails open.
- Any path inside the repository now works as `repoPath`: it is resolved to the
  repo root (`git rev-parse --show-toplevel`), so monorepo subdirectory paths no
  longer silently produce zero matches.
- Path normalization: leading `./` segments are stripped, and absolute paths in
  a sibling directory sharing a name prefix (`repo` vs `repo2`) are no longer
  mis-relativized; repo-root matching is case-insensitive on Windows only.
- A corrupt `.unwritten/index.json` triggers a silent rebuild instead of
  crashing every subsequent command.
- `git cat-file --batch` stdin is now UTF-8, so non-ASCII file paths no longer
  silently drop out of member-level training on Windows.
- The "no visible edit" guard now compares content with normalized line
  endings: with `core.autocrlf` the working tree (CRLF) never matched the blob
  (LF) byte-for-byte, which re-enabled the cosmetic-suppression false positive
  on Windows.
- Index saves use a unique temp file name — concurrent saves (MCP server +
  pre-commit hook) can no longer collide.

### Changed

- MCP responses are compact JSON (fewer tokens per call).
- `check_holes` clamps an explicit `minConfidence` below 0.3 (with a note) —
  validated as ~90% noise.

## [0.2.1] - 2026-07-12

### Added

- README badges: NuGet version, downloads, license (same style as McpOrchestrator).
- Project icon (`img/`): embedded as the NuGet package icon and listed in the
  MCP Registry manifest (`icons` in `server.json`).

## [0.2.0] - 2026-07-10

### Changed

- **Project renamed from ConventionSense to Unwritten.** New NuGet package ID
  `Unwritten`, tool command `unwritten`, MCP server name
  `io.github.Byggarepop/unwritten`, index directory `.unwritten/`, and repository
  URL <https://github.com/Byggarepop/Unwritten>. Versions ≤ 0.1.2 were published
  as `ConventionSense`; earlier changelog entries below refer to the old name.

## [0.1.2] - 2026-07-09

### Changed

- README (also shown on nuget.org): plain-language "what it adds" and
  thresholds sections, two-step quick start, per-client MCP setup
  (Claude Code, VS Code, Visual Studio 2026), a ready-to-copy capability
  description for MCP gateways like McpOrchestrator, and all examples
  using `dotnet tool execute` — no install step. Docs only; no code changes.

## [0.1.1] - 2026-07-08

### Fixed

- MCP Registry listing: `server.json` description shortened to the registry's
  100-character limit (the v0.1.0 registry publish was rejected with a 422).

## [0.1.0] - 2026-07-08

### Added

- Change-coupling indexer over git history: non-merge commits touching ≤ 30 files,
  Wilson lower bound scoring, alert floor 0.6, minimum support 10, incremental
  updates keyed by HEAD (`.conventionsense/index.json`).
- MCP server (`conventionsense mcp`) with five tools: `check_holes`,
  `expected_companions`, `explain_rule`, `stats`, `reindex` — every alert carries
  counts, confidence, and example commits as evidence.
- Pre-commit CLI: `conventionsense check --staged` (exit 1 at ≥ 0.7, configurable
  via `--fail-at`), plus `stats` and `reindex` subcommands.
- Per-repo configuration in `.conventionsense/config.json`; CLI flags override
  config, config overrides defaults.
- Content-aware exception layer for JSON triggers: learns which top-level keys
  ("facets") predict each rule and suppresses alerts for non-predictive edits
  (e.g. pure version bumps). Suppressed holes are excluded from the exit code;
  `--strict` re-includes them. Fail-open everywhere.
- Opt-in C# member-level detection (`"memberLevel": true`): members as entities
  (`Namespace.Type.Member/arity`, survives file renames), Roslyn syntax-only
  diffing, member holes in `check_holes` and the CLI, cosmetic-edit suppression
  (whitespace/comment-only C# edits), 5,000-commit training window.
- Packaged as a dotnet tool (`ConventionSense`, command `conventionsense`).

### Notes

- Renamed from the working title "Lacuna"; the original spec document
  (`lacuna-claude-code-prompt.md`) keeps the old name as a historical record.
