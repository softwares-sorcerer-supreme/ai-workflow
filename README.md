# ai-workflow

`ai-runner` is a small .NET worker. It runs on the developer laptop (VPN + internet),
polls GitBucket, runs GitHub Copilot CLI, and writes results back to GitBucket.
See the design document for the full picture.

| Workflow | Trigger | What the runner does |
| --- | --- | --- |
| 1 Analysis | new issue with label `ai`, or a comment with `@ai` | Copilot (read-only) writes questions, assumptions, plan, complexity; when ready also the SPEC |
| 1b Breakdown | human adds `ai-breakdown` on an `ai:ready` + `ai:complex` issue | creates `feat/issue-{n}` and one sub-issue per sub-task; starts the next sub-issue when one is merged; opens the final PR |
| 2 Implement | human adds `ai-implement` on an `ai:ready` + `ai:simple` issue or sub-issue | Copilot writes code on `ai/issue-{n}`; the runner checks build/test, commits, pushes, opens the PR |
| 3 PR review | new PR or new head commit | Copilot (read-only) writes one review comment, updated in place |
| 4 GitHub fallback | `needs-cloud-agent` | **not built yet** - the label is only respected (the runner skips the issue) |

## First run

```powershell
cd C:\dev\ai-workflow
dotnet user-secrets set GitBucket:Token <ai-bot token> --project src/AiRunner
# only if git over HTTP needs a password other than the token:
# dotnet user-secrets set GitBucket:GitPassword <password> --project src/AiRunner

# Start copilot as a child process and capture its output (no GitBucket needed).
dotnet run --project src/AiRunner -- --copilot-test

# Create the labels from the design doc in every repo of repos.yml.
dotnet run --project src/AiRunner -- --setup-labels

# Poll once and print what the runner would do. Runs nothing, changes nothing.
dotnet run --project src/AiRunner -- --once --dry-run

# Poll once, run the queued jobs, exit.
dotnet run --project src/AiRunner -- --once

# Normal mode: poll every pollIntervalSeconds and run jobs until Ctrl+C.
dotnet run --project src/AiRunner
```

The runner finds `repos.yml` in the current folder or a parent folder (or `--config <path>`).
At start it copies `agents/` and `skills/` to `~/.copilot/`, so Copilot always uses the
version checked out here. A product repo's own `.github/agents/` copy wins over these.

Tests (no VPN, no premium requests; GitBucket, git and Copilot are faked):

```powershell
dotnet test ai-workflow.slnx
```

## Onboard a repo

1. Copy `onboarding/AGENTS.md` to the repo root and fill it in (build and test commands, folder map, conventions, never-edit folders).
2. Add the repo to `repos.yml`: `workflows`, `models`, and `commands.setup` / `build` / `test` (the same commands as in `AGENTS.md`; any language).
3. `dotnet run --project src/AiRunner -- --setup-labels`.
4. Put the label `ai` on an issue and watch `--once --dry-run`.

With `review.scope: all`, the first start reviews **every PR that is open at that moment** (one Copilot run each).
Start with `scope: scoped`, or put `[skip-ai]` on old PRs, if that is too many.

## How a human drives it

- **Answer the analysis**: reply by number (`Q1: CSV only`, `A2: wrong, it is ...`) and write `@ai` in the comment. Comments without `@ai` cost nothing.
- **Approve a breakdown**: add `ai-breakdown` (needs `ai:ready` + `ai:complex`). Change it first by replying with `@ai`.
- **Start the code**: add `ai-implement` (needs `ai:ready` + `ai:simple`). Sub-issues after the first start by themselves when the previous PR is merged.
- **Skip a review**: start the PR title with `[skip-ai]`, or keep it a draft.
- **Retry after `ai:failed`**: read the comment (and the log folder it names), fix the cause, then remove `ai:failed`.
  An analysis that stopped at the round limit stops again on retry: clarify in a call, or raise `limits.maxAnalysisRounds`.
- **Merge**: always a human. The runner never merges.

## Architecture

One pattern everywhere: **Poll -> Plan -> Queue -> Job**.

```
src/AiRunner/
  Program.cs        DI registration and command-line modes only
  Configuration/    RunnerConfig: repos.yml
  Core/             pure logic, no I/O: JobPlanner, SubIssueSequencer, parsers (analysis, breakdown, review, sub-issue body),
                    ContextBuilder, Markers, AsciiSanitizer, Labels/Branches, GitBucket models
  Infrastructure/   every side effect behind an interface: GitBucketClient, CopilotRunner, GitClient,
                    ProcessRunner, Workspace, SqliteStateStore, CostLog, JobLog, AgentInstaller
  Jobs/             one class per workflow step: AnalyzeIssue, BreakdownIssue, ImplementIssue,
                    ReviewPullRequest, SequenceSubIssues. A job orchestrates: reads GitBucket,
                    asks Core to decide or parse, calls Infrastructure
  Hosting/          Poller (GitBucket -> JobPlanner -> JobQueue), JobQueue (SQLite-backed, one pending
                    per key), JobWorker (one job at a time, timeout, failure comment + ai:failed, cost row)
tests/AiRunner.Tests/
  Core/             unit tests of the pure logic
  Jobs/, Hosting/   each job and the worker with in-memory fakes (Fakes/)
```

Rules: `Core` depends on nothing (only the config POCOs). Decisions and text parsing go to `Core`,
side effects to `Infrastructure`, orchestration to `Jobs`, scheduling to `Hosting`.
Every job first runs `JobPlanner` again on fresh data and exits quietly if the work is no longer needed,
so a duplicate or late job is harmless.

Shared files:

| Folder | Used how |
| --- | --- |
| `agents/` | `grill-issue`, `implement-issue`, `pr-review`; copied to `~/.copilot/agents/` |
| `skills/` | `grill-me`, `review-checklist`; copied to `~/.copilot/skills/` |
| `templates/` | analysis (F), SPEC (G), review (H); copied into each work folder as `.ai-workflow/templates/`, never committed |
| `onboarding/AGENTS.md` | starting point for a product repo |

Security limits (what the runner does and does not protect):

- Copilot runs without GitBucket/GitHub tokens in its environment and without the GitHub MCP server.
  Analysis and review deny `write` and `shell`; implement allows `write` and only the configured commands, never git.
- Every child process (Copilot, git, setup/build/test) runs without the token variables. git gets the token
  through `GIT_CONFIG_*` variables (not the command line, not `.git/config`), with hooks and fsmonitor off.
  If `.git/config` changed during an implement run, the clone is deleted and nothing is pushed.
- **Setup/build/test run code that the agent wrote, with your user rights.** That code can read anything you can,
  including the user-secrets file. The runner limits the damage (no tokens in the environment, a human reviews
  every PR) but it is not a sandbox. Do not run the implement workflow on a machine with production access.
- Bot comments are recognised by a hidden marker. Since the runner posts with your account, a human who pastes a
  bot comment (with the marker) is treated like the bot. Every bot comment starts with a visible `**[ai-runner]**`.

Local files (git-ignored): `work/` (one clone per repo), `logs/jobs/<run>/` (context, raw Copilot output,
extracted blocks; 30 days), `logs/cost.csv` (one row per job; if it is open in Excel the row is only in the database), `ai-runner.db`
(what the poller last saw per issue, handled comments, reviewed commits, job queue). Deleting `ai-runner.db` is safe: the runner finds its own comments by their markers.

## GitBucket facts (checked in Phase 0)

- `since` filter on the issue list is ignored -> the poller compares `updated_at` itself.
- Comments with non-ASCII characters fail with HTTP 500 -> every text is cleaned by `AsciiSanitizer`; templates are ASCII.
- `DELETE` comment fails -> never delete; `PATCH` comment works (the review is updated in place).
- `/pulls/{n}/files` does not exist -> use `git diff base...head` on the local clone.
- Bot comments are marked with the hidden line `<!-- ai-runner -->`; analysis, SPEC and review comments keep
  their `<!-- X_START -->` / `<!-- X_END -->` markers, so the runner recognises them without the database.

## Still to verify on the real server

These calls are new since Phase 0; check them once with `--once` on a test issue:

- `GET /user`, `GET /repos/{o}/{r}` (`default_branch`), `POST /repos/{o}/{r}/labels`.
- Paging (`page`, `per_page`) on issues, comments and pulls; the `assignee` filter. The client reads pages until
  one brings nothing new, so a server that ignores `page` costs one extra call per list.
- Whether adding/removing a label changes `updated_at` (the poller also compares labels, so either way works).
- `POST /repos/{o}/{r}/issues` with `labels` (the runner adds missing labels afterwards anyway).
- `POST /repos/{o}/{r}/pulls` with `head` = branch name; `merged` / `merged_at` on closed PRs.
- git over HTTP with `login:token` basic auth (else set `GitBucket:GitPassword`), and `refs/pull/{n}/head`
  (the review falls back to the branch when it is missing).
- The exact Copilot CLI error text when the premium requests are used up (`CopilotRunner.LooksLikeQuotaError`).
