# MaxTransactionSize revalidation — 2026-07-14

Question: is the 30-file training cap (`maxTransactionSize`) too narrow now that
agentic workflows produce larger single-intent commits?

Method: `eval/Unwritten.Eval` replay harness. Per repo: train one index per cap
{20, 30, 50, 75, 100, 200} on all history before the last 2,000 non-merge
commits, then replay those held-out commits oldest-first (evaluate → ingest,
incremental as in real use). A flagged hole counts as **filled** if the missing
companion changes within the next 10 held-out commits (the original prototype's
precision proxy). **Marginal** = holes a raised cap flags that cap 30 did not
flag on the same commit at the same floor — i.e. the precision of exactly the
rules the cap raise buys. Eval indexes use `MaxExamplesPerPair = 1`; commits
>200 files are ingested but not evaluated; the 10 tail commits are ingest-only.

Subjects: dotnet/efcore (anchor, matches the original validation),
openai/codex and cline/cline (agent-era, heavily AI-authored history).
Raw numbers in `efcore.csv`, `codex.csv`, `cline.csv`.

## Harness sanity check

EF Core, cap 30, floor 0.7: **67.6%** fill — the original prototype measured
**66.5%** on the same repo three years of history ago. The protocol replicates.

## Commit sizes did shift (in agentic repos)

Share of non-merge commits excluded by cap 30, training window vs the most
recent 2,000 commits:

| repo | older history | recent 2,000 |
|---|---:|---:|
| cline | 1.2% | **5.9%** |
| codex | 3.9% | **7.0%** |
| efcore | 8.1% | 6.3% |

The agent-era repos' commits really are getting bigger (~5× / ~2×). EF Core's
are not — its recent commits are *smaller* than its 2010s refactor era.

## But the marginal rules only pay off in agentic repos

Fill rate at floor 0.6 (all flagged holes / marginal-only), holdout of ~1,980
evaluated commits per repo:

| repo | cap 30 overall | cap 50 marginal | cap 100 marginal | cap 200 marginal |
|---|---:|---:|---:|---:|
| cline  | 39.7% | **48.6%** (35 holes) | 34.5% (113) | 36.5% (137) |
| codex  | 20.1% | 15.1% (232) | 23.8% (370) | 23.8% (416) |
| efcore | 55.8% | **9.1%** (44) | 9.7% (134) | **5.6%** (285) |

- **efcore**: raising the cap is strictly harmful — marginal rules fill at
  5–10% vs 56% baseline (pure noise), and the overall fill rate decays
  monotonically with the cap (55.8% → 42.2% at cap 200). The 30 cap is doing
  exactly its job in a human-era repo.
- **cline**: cap 50's marginal rules *beat* the baseline (48.6% vs 39.7%);
  beyond 50 they regress to baseline-ish. Consistent with "40–60-file
  single-intent agent commits carry real convention signal".
- **codex**: mixed — marginal rules slightly below baseline at cap 50, slightly
  above at 100+; at floor 0.8 they outperform (35–42% vs 30.9%) and cap 50
  nearly quadruples the rule count at that floor (49 → 187).

## Verdict

Per the pre-registered decision rule (raise the default only if marginal rules
stay within ~5 points of the cap-30 fill rate at floor 0.6 **across subjects**):
**keep the default at 30.** EF Core fails the rule catastrophically at every
raised cap, and the default has to be safe for both populations.

The cap was, however, worth revalidating: it is now *evidence* that the right
value is repo-dependent, not a 2004 convention taken on faith:

- Human-era / long-history repos: 30 is right (and the harness confirms the
  original 0.6/0.7 floor calibration still holds).
- Agent-heavy repos: `"maxTransactionSize": 50` in `.unwritten/config.json` is
  a defensible, evidence-backed override — real signal, little noise. Beyond
  50 the returns fade in every subject.

Future work: a repo-adaptive cap (or 1/size pair down-weighting instead of a
hard cutoff) could capture cline-style gains without EF-Core-style noise;
`memberMaxTransactionSize = 50` (set by analogy) could be swept with the same
harness once member-level replay is wired up.

Reproduce: `dotnet run --project eval/Unwritten.Eval -c Release -- --repo <path-to-clone> --csv out.csv`
(blob-less bare clones work: `git clone --bare --filter=blob:none <url>`).
