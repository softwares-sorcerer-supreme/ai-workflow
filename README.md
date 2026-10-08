# ai-workflow

`ai-runner` is a small .NET worker. It runs on the developer laptop (VPN + internet),
polls GitBucket, runs GitHub Copilot CLI, and writes results back to GitBucket.
See the design document for the full picture.

| Workflow | Trigger | What the runner does |
| --- | --- | --- |
| 1 Analysis | new issue with label `ai`, or a comment with `@ai` | Copilot (read-only) writes questions, assumptions, plan, complexity; when ready also the SPEC |
| 1b Breakdown | human adds `ai-breakdown` on an `ai:ready` + `ai:complex` issue | creates `feat/issue-{n}` and one sub-issue per sub-task; starts the next sub-issue when one is merged; opens the final PR |
| 2 Implement | human adds `ai-implement` on an `ai:ready` + `ai:simple` issue or sub-issue | Copilot writes code on `ai/issue-{n}` and goes through its stages itself (verify with build/test/lint, review and simplify with the `code-reviewer` subagent, PR_BODY.md); the runner re-checks build/test/lint and PR_BODY.md, sends problems back for up to `implement.maxFixRounds` fix rounds, then commits, pushes, opens the PR (WIP if checks still fail); work from a failed or too-big run is still pushed to the branch (no PR) and a retry continues from it |
| 3 PR review | new head commit on a PR labelled `ai` or opened by the runner (`review.scope`) | Copilot (read-only) writes one review comment, updated in place |
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

PR review is opt-in by default (`review.scope: scoped`): a PR is reviewed when it has the label `ai`, or when this
runner opened it itself (head `ai/*` or `feat/issue-*`, author = the runner's user). With `scope: all` every open PR
is reviewed, including colleagues' PRs and **every PR open at the first start** (one Copilot run each), and each runner
pointed at the repo reviews it again.

## How a human drives it

- **Answer the analysis**: reply by number (`Q1: CSV only`, `A2: wrong, it is ...`) and write `@ai` in the comment. Comments without `@ai` cost nothing.
- **Approve a breakdown**: add `ai-breakdown` (needs `ai:ready` + `ai:complex`). Change it first by replying with `@ai`.
- **Start the code**: add `ai-implement` (needs `ai:ready` + `ai:simple`). Sub-issues after the first start by themselves when the previous PR is merged.
- **Ask for a review**: add the label `ai` to the PR (PRs the runner opened itself are reviewed anyway).
  Every new commit is reviewed again; to stop, remove the label, start the title with `[skip-ai]`, or make it a draft.
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
| `agents/` | `grill-issue`, `implement-issue`, `code-reviewer` (read-only subagent of implement-issue), `pr-review`; copied to `~/.copilot/agents/` |
| `skills/` | `grill-me`, `implement-checklist`, `review-checklist`; copied to `~/.copilot/skills/` |
| `templates/` | analysis (F), SPEC (G), review (H), PR_BODY.md of the implement agent; copied into each work folder as `.ai-workflow/templates/`, never committed |
| `onboarding/AGENTS.md` | starting point for a product repo |

Security limits (what the runner does and does not protect):

- Copilot runs without GitBucket/GitHub tokens in its environment and without the GitHub MCP server.
  Analysis and review deny `write` and `shell`; implement allows `write` and the tools of the configured setup/build/test/lint
  commands with any arguments (`dotnet build X.sln` -> `shell(dotnet:*)`; shells like `powershell` keep the exact command), never git.
  This is a guard rail, not a boundary: a tool like `npm` or `make` can still run other programs (`npm exec`, a Makefile target).
  `write(.ai-workflow)` is denied as well; whether Copilot applies that to the files inside is not confirmed, so the runner
  also re-copies the folder on every run and never commits it.
- Every child process (Copilot, git, setup/build/test) runs without the token variables. git gets the token
  through `GIT_CONFIG_*` variables (not the command line, not `.git/config`), with hooks and fsmonitor off.
  If `.git/config` changed during an implement run, the clone is deleted and nothing is pushed.
- **Setup/build/test/lint run code that the agent wrote, with your user rights.** That code can read anything you can,
  including the user-secrets file. The runner limits the damage (no tokens in the environment, a human reviews
  every PR) but it is not a sandbox. Do not run the implement workflow on a machine with production access.
- **Cost and time of fix rounds.** `implement.maxFixRounds` (default 2) adds up to that many Copilot runs per implement job
  (1 premium request each) and raises the job timeout to `implementTimeoutMinutes * (2 + 2 * maxFixRounds) + 10` minutes.
  When only PR_BODY.md is wrong, at most one fix round runs. A fix round is not started when too little job time is left,
  and a failed fix round still opens the PR from what exists. Set it to 0 to keep the old behaviour (one run, WIP PR when red).
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
