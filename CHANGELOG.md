# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
