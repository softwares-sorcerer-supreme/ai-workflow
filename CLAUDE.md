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
Review fix (workflow `fix`, `FixReviewJob`): a new `@ai` comment on a PR in that same scope (whatever `review.scope` says) makes
Copilot change the PR's branch; it is planned before the review, and the pushed commit is then reviewed as usual.

## Commands

```powershell
dotnet run --project src/AiRunner -- --once --dry-run   # poll once, print planned jobs, change nothing
dotnet run --project src/AiRunner -- --once             # poll once, run queued jobs, exit
dotnet run --project src/AiRunner -- --setup-labels     # create the workflow labels in every repo
dotnet run --project src/AiRunner -- --copilot-test     # start copilot as a child process
dotnet run --project src/AiRunner -- --attachment-test <url>   # download one _attached file, show the answer (no Copilot)
```

- Tests need no network: GitBucket, Copilot and processes are faked. `GitWorkspaceTests` uses real `git` against a `file://` bare repo.
- Do not run `--copilot-test`, `--once` (without `--dry-run`), `--attachment-test` or the loop yourself: they spend Copilot premium requests
  and/or touch the real GitBucket. They also need the token (`dotnet user-secrets set GitBucket:Token ... --project src/AiRunner` or env `GITBUCKET_TOKEN`)
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

- **Attachments.** Files people link from human text (`/_attached/<id>` on the GitBucket host; the JSON has no attachment list) are
  downloaded by `Jobs/AttachmentFetcher` right after `IWorkspace.PrepareAsync` (the next `git clean` removes them) into
  `.ai-workflow/attachments/`, and `AttachmentReport` appends an "Attachments" section to context.md / plan.md. Analyze, review,
  implement and review fix all use it; bot comments are skipped. `GitBucketClient.DownloadAttachmentAsync` is the only code that
  knows the quirks: a second `HttpClient` that never follows redirects (a redirect to the login page = "token not accepted"), the
  token goes only to the GitBucket host, the size cap is enforced while streaming. Images and PDFs also go to Copilot with
  `--attachment` (round 1 only) when `ICopilotRunner.CanAttachFilesAsync` finds the flag in `copilot --help`; older CLIs only get the
  files on disk. A file that cannot be had becomes a line in the section, never a failed job. `--attachment-test <url>` checks the
  download against the real server (no Copilot).
- **Jobs re-plan.** Every job starts with `JobLoader.LoadIssueAsync` (or `LoadPullAsync`), which re-runs `JobPlanner.PlanIssue` on fresh data; if the decision is
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
- **Implement = agent stages + runner safety net.** `agents/implement-issue.agent.md` makes the agent verify (build/test/lint),
  review and simplify via the read-only `code-reviewer` subagent, and write PR_BODY.md before it stops. `ImplementIssueJob` then re-runs
  the checks and `PrBodyParser`; `ImplementGate` decides on a fix round (`fix.md` from `ContextBuilder.ForFix` + `diff.patch`, max
  `implement.maxFixRounds`, each a premium request; at most 1 when only PR_BODY.md is wrong; none when job time is short or a fix
  round's Copilot call failed, which still opens the PR; job time comes from `JobRun.TimeLeft`, set by `JobWorker`). A fix round
  that changed no code keeps the previous checks. Only a round-1 Copilot failure fails the job. Logs per round:
  `round-N-copilot-*`, `round-N-transcript.md` (`--share`), `round-N-check-M.txt`, `fix-N.md` (input of round N).
  The loop lives in `Jobs/AgentSession`, and `FixReviewJob` reuses all of it: same agent (`implement-issue`, its "Review fix"
  section), same PR_BODY.md and `PrBodyParser`; only plan.md differs (the review + new comments, `ContextBuilder.PlanForReviewFix`;
  each request is one acceptance criterion). A new quality layer goes into the agent stages or `AgentSession` once and both flows get it.
  The jobs keep what happens around it (implement: save work, size limits, PR; fix: commit + non-forced push to the PR branch, reply
  comment). A fix whose checks are still red is never pushed to the PR: it goes to `ai/pr-N-fix-K` (`Branches.ReviewFix`) and the reply
  says so (outcome `red`); a fix is refused up front when the repo has no build/test/lint command (`WhyNotFix`). Only a round-1
  Copilot failure fails a fix, and then nothing is pushed.
- **Review fix bookkeeping.** The reply comment is a `REVIEW_FIX` block with `context-upto` (`AtAiThread.Reply`, the one place that writes and reads the tag, counts replies against the limits and
  owns the rule "what is new"; see `CONTEXT.md`);
  `AtAiThread` (`Core`) finds new `@ai` comments after it (or after the local last-handled id), the same
  rule the analysis rounds use. Refusals (limit, push target not the PR's own branch at its head) are `JobPlanner.WhyNotFix`. The requests in plan.md start after the reply
  only, never after the local id, so a failed fix's requests reach the next fix; plan.md also carries the last
  reply. `limits.maxReviewFixes` counts the replies. A fix queued while another runs waits and then sees the new head.
  A failed fix marks its `@ai` comment handled in `OnFailed` (there is no `ai:failed` on PRs), so it is not retried every poll;
  `JobInfo.Retry` tells the human to write `@ai` again. Poller and PR jobs plan PRs through `JobLoader.PlanPullAsync` /
  `LoadPullAsync`, which read comments only when `PlanPull` says `NeedsComments`. `Markers.Wrap` neutralises `<!--` in the
  block text, so agent output can never fake a marker or a `context-upto` tag.
- **Agent output contract.** `templates/` (ASCII) define what agents write; `Core` parsers validate it strictly (status/complexity/verdict/severity
  vocabularies, the 5-column breakdown table `| # | Title | Scope | Touches | Depends on |` + `Shared contract:`, `[developer]` title prefix).
  Changing a template means changing its parser, the agent file in `agents/`, and `tests/AiRunner.Tests/Samples.cs` together.

## GitBucket and security constraints

- GitBucket returns HTTP 500 on non-ASCII text: all outgoing text goes through `AsciiSanitizer` (done in `GitBucketClient`); keep templates ASCII.
- `since` on issue lists is ignored; comment `DELETE` fails (use `PATCH`, never delete); `/pulls/{n}/files` does not exist (use `git diff` on the clone).
  Lists are paged with `per_page=100&page=n` until a page brings nothing new.
- `ProcessRunner` strips token env vars from every child process. git gets auth via `GIT_CONFIG_COUNT/KEY_i/VALUE_i` env vars
  (http.extraHeader, hooks and fsmonitor off) — never put tokens on a command line or in `.git/config`.
  `AgentSession` (implement and review fix) discards the clone and pushes nothing if `.git/config` changed during the run.
- Copilot permissions: analyze/review deny `write` and `shell`; implement and fix allow `write` plus the *tools* of the repo's configured
  setup/build/test/lint commands with any arguments (`Core/ShellPermissions`: `dotnet build X.sln` -> `shell(dotnet:*)`, launchers keep
  the exact command), never git; `write(.ai-workflow)` is denied (whether it covers files inside is unconfirmed). In `-p` mode nobody
  approves a command, so a narrow pattern silently blocks the agent. This is a guard rail, not a boundary: `npm exec`, a Makefile
  target or agent-edited build files can run anything; the real protections are no tokens in the env and the `.git/config` guard.

## Conventions

- Stay stack-agnostic: nothing may assume .NET (or any stack) in the product repos. Setup/build/test commands come from `repos.yml`
  `commands`; agent files, skills and `onboarding/AGENTS.md` stay generic.
- Code style: file-scoped namespaces, `sealed` classes, primary constructors, records, short XML summaries that explain *why*, plain simple English.
- Readability rules (`.editorconfig` enforces the first as a build warning, IDE0011):
  - **Braces always.** `if`, `else`, `foreach`, `for`, `while`, `try`, `catch`, `finally` and `lock` bodies get braces on their own lines,
    even for one statement. No `if (x) return;` and no `try { ... } catch { ... }` on one line. An empty `catch` holds a comment saying why.
  - **Blank lines between paragraphs.** Separate the steps of a method (read inputs, compute, log, act), put a blank line after a group
    of guard clauses, around every multi-line block, and before a final `return` that follows multi-line logic. Related one-liners stay together.
  - **One call per line** in a chain of three or more calls, indented 4 spaces from the statement start. Wrap lines over 120 characters.
  - **Names say what they hold.** No single-letter names and no cryptic abbreviations: `cancellationToken` not `ct`, `stopwatch` not `sw`,
    `exception` not `ex`, `directory` not `dir`, `logger` not `log`, `issue => issue.Number` not `i => i.Number`. Booleans start with
    `is`, `has`, `can` or `should`. Short, clear words stay (`repo`, `config`, `id`, `sha`, `url`).
  - **A result goes into a variable first.** When a call or `await` gives a value that is passed to another call, put it in a named local
    and pass the local: `var planFile = Path.Combine(directory, "plan.md"); await File.WriteAllTextAsync(planFile, plan, token);`.
    Never hoist a call out of a lazy position (the right side of `??`, `?:`, `&&`, `||`, `?.`, a lambda, a loop condition, an `else if`
    condition): write an `if` instead, so the same calls run in the same order. Not covered: lambdas, `new X(...)` itself, `nameof`/`typeof`,
    text inside `$"{...}"`, one fluent chain, field initializers and attributes. In tests, `Assert.*` lines only extract a two-level nest.
  - **More than 3 parameters means a DTO.** A method takes at most 3 parameters, not counting a `CancellationToken` or the receiver of an extension
    method; more data goes into a `sealed record` next to the method (`WorkspaceRequest`, `NewPull`, `IssuePlanInput`...), built with named
    arguments. Constructors that receive injected services are exempt, and so are record members; services are not bundled into a DTO.
    `tests/AiRunner.Tests/CodeRules/ParameterCountTests.cs` fails the build when a method breaks this.
  - Short `=>` members and lambdas are fine; use a block body when the expression spans several lines or needs a local.
  - Renaming covers locals, parameters, lambda parameters, private fields and constructor parameters only. Never rename JSON/YAML/SQLite/CLI-bound
    members, template text or markers just for style.
- Tests mirror the layers (`Core/`, `Jobs/`, `Hosting/`, `Infrastructure/`). Job tests use `JobTestKit` (`Fakes/Fakes.cs`, `FakeGitBucket`)
  and build data with `Samples.Issue/Human/Bot` and the filled templates in `Samples.cs`.
- `SQLitePCLRaw.lib.e_sqlite3` is pinned in the csproj to fix NU1903; don't drop it.
