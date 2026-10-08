# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`ai-runner` (`src/AiRunner`, .NET 10 Worker Service) runs on a developer laptop. It polls a GitBucket server (GitHub-compatible `/api/v3`),
runs GitHub Copilot CLI (`copilot -p --agent ...`) for issue analysis, breakdown into sub-issues, implementation and PR review,
then writes the results back as comments, labels, branches and PRs. The design doc lives one folder up:
`../AI Dev Workflow – Design Document (GitBucket + Jenkins + Copilot CLI).md`. Workflow 4 (GitHub mirror / cloud agent) is
intentionally not built; `needs-cloud-agent` and `ai:on-github` only make the planner skip an issue.
PR review is opt-in: with the default `review.scope: scoped` only PRs labelled `ai` and PRs the runner opened itself from
`ai/*` / `feat/issue-*` are reviewed (`JobPlanner.InScope`); `scope: all` reviews every open PR.

## Commands

```powershell
dotnet build ai-workflow.slnx
dotnet test ai-workflow.slnx
dotnet test ai-workflow.slnx --filter "FullyQualifiedName~JobPlannerTests"            # one class
dotnet test ai-workflow.slnx --filter "FullyQualifiedName~JobPlannerTests.New_issue_gets_round_1"  # one test

dotnet run --project src/AiRunner -- --once --dry-run   # poll once, print planned jobs, change nothing
dotnet run --project src/AiRunner -- --once             # poll once, run queued jobs, exit
dotnet run --project src/AiRunner -- --setup-labels     # create the workflow labels in every repo
dotnet run --project src/AiRunner -- --copilot-test     # start copilot as a child process
```

- Tests need no network: GitBucket, Copilot and processes are faked. `GitWorkspaceTests` uses real `git` against a `file://` bare repo.
- Do not run `--copilot-test`, `--once` (without `--dry-run`) or the loop yourself: they spend Copilot premium requests and/or write to the
  real GitBucket. They also need the token (`dotnet user-secrets set GitBucket:Token ... --project src/AiRunner` or env `GITBUCKET_TOKEN`)
  and VPN; leave these to the user.
- `repos.yml` is found in the current folder or a parent (or `--config <path>`); `paths` in it are relative to its folder.

## Architecture: Poll -> Plan -> Queue -> Job

One project, folder = namespace, dependency direction `Hosting -> Jobs -> Infrastructure + Core`:

- `Core/` — pure logic only: no I/O, no DI, no async. All decisions (`JobPlanner`, `SubIssueSequencer`) and all text parsing
  (`AnalysisParser`, `BreakdownParser`, `ReviewParser`, `SubIssueBody`, `Markers`, `MarkdownSections`) and naming (`Labels`, `Branches`,
  `BotComments`) go here. May depend only on the `Configuration/` POCOs.
- `Infrastructure/` — every side effect behind a small interface (`IGitBucketClient`, `ICopilotRunner`, `IGitClient`, `IProcessRunner`,
  `IWorkspace`, `IStateStore`, `ICostLog`). No business decisions; a GitBucket/CLI quirk is fixed in exactly one class.
- `Jobs/` — one `IJob` per workflow step. A job orchestrates: load fresh data, ask Core, call Infrastructure. Expected failures throw `JobFailedException`.
- `Hosting/` — `Poller` (GitBucket -> `JobPlanner` -> `JobQueue`), `JobQueue` (SQLite-backed, one pending job per key), `JobWorker`
  (one job at a time, timeout, failure comment + `ai:failed`, cost row). Scheduling and lifetime only.
- `Program.cs` — DI registration and command-line modes only.

Key mechanics that span several files:

- **Jobs re-plan.** Every job starts with `JobSupport.LoadIssueAsync`, which re-runs `JobPlanner.PlanIssue` on fresh data; if the decision is
  no longer this job's `Type`, it returns `JobOutcome.NotNeeded(Quiet)` and the worker deletes the job row and its log folder.
  So duplicate or late jobs are harmless, and planner rules must stay the single source of "should this run".
- **Job metadata** (workflow name, timeout, description, PR vs issue) lives on the job as `IJob.Info`; per-job failure bookkeeping goes
  in `IJob.OnFailed`. `JobWorker` must not switch on job type.
- **Adding a job type:** add to `JobType` (`Core/JobRequest.cs`), add the rule in `JobPlanner`, implement `IJob`, register it in `Program.cs`.
- **Poller cost control:** issue fingerprints (updated_at + labels) are stored per issue; comments are fetched only when the plan
  returns `NeedsComments` and the issue changed. `PlanDecision.Routine` decisions log at Debug.
- **No database dependency for correctness.** Bot comments carry a hidden `<!-- ai-runner -->` marker plus a visible `**[ai-runner]**` line;
  analysis/SPEC/review comments keep their `<!-- X_START -->` / `<!-- X_END -->` markers; the parent's sub-issue list is
  `<!-- SUBISSUES: ... -->`. The runner finds its own comments by these, so deleting `ai-runner.db` is safe — keep it that way.
- **Agent output contract.** `templates/` (ASCII) define what agents write; `Core` parsers validate it strictly (status/complexity/verdict/severity
  vocabularies, the 5-column breakdown table `| # | Title | Scope | Touches | Depends on |` + `Shared contract:`, `[developer]` title prefix).
  Changing a template means changing its parser, the agent file in `agents/`, and `tests/AiRunner.Tests/Samples.cs` together.

## GitBucket and security constraints

- GitBucket returns HTTP 500 on non-ASCII text: all outgoing text goes through `AsciiSanitizer` (done in `GitBucketClient`); keep templates ASCII.
- `since` on issue lists is ignored; comment `DELETE` fails (use `PATCH`, never delete); `/pulls/{n}/files` does not exist (use `git diff` on the clone).
  Lists are paged with `per_page=100&page=n` until a page brings nothing new.
- `ProcessRunner` strips token env vars from every child process. git gets auth via `GIT_CONFIG_COUNT/KEY_i/VALUE_i` env vars
  (http.extraHeader, hooks and fsmonitor off) — never put tokens on a command line or in `.git/config`.
  `ImplementIssueJob` discards the clone and pushes nothing if `.git/config` changed during the run.
- Copilot permissions: analyze/review deny `write` and `shell`; implement allows `write` plus only the repo's configured commands, never git.

## Conventions

- Stay stack-agnostic: nothing may assume .NET (or any stack) in the product repos. Setup/build/test commands come from `repos.yml`
  `commands`; agent files, skills and `onboarding/AGENTS.md` stay generic.
- Code style: file-scoped namespaces, `sealed` classes, primary constructors, records, short XML summaries that explain *why*, plain simple English.
- Tests mirror the layers (`Core/`, `Jobs/`, `Hosting/`, `Infrastructure/`). Job tests use `JobTestKit` (`Fakes/Fakes.cs`, `FakeGitBucket`)
  and build data with `Samples.Issue/Human/Bot` and the filled templates in `Samples.cs`.
- `SQLitePCLRaw.lib.e_sqlite3` is pinned in the csproj to fix NU1903; don't drop it.
