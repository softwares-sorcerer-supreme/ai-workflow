# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`ai-runner` (`src/AiRunner`, .NET 10 Worker Service) runs on a developer laptop. It polls a GitBucket server (GitHub-compatible `/api/v3`),
runs GitHub Copilot CLI (`copilot -p --agent ...`) for issue analysis, breakdown into sub-issues, implementation and PR review,
then writes the results back as comments, labels, branches and PRs. The design doc lives one folder up:
`../AI Dev Workflow – Design Document (GitBucket + Jenkins + Copilot CLI).md`. Workflow 4 (GitHub mirror / cloud agent) is
intentionally not built; `needs-cloud-agent` and `ai:on-github` only make the planner skip an issue.
Work is organised in **projects** (`docs/specs/projects-and-components.md`): a project groups **components** (repo + optional folder),
every job checks out all of them under `work/<project>/` and changes code in one. Issues and sub-issues live in the project's
`issues_in` component only; PRs are polled in every repo that holds a `code: true` component. `repos.yml` is snake_case and strict
(an unknown key stops the start); `defaults:` is merged into each project (objects key by key, lists replace); the old flat
`repos:` list becomes one-component projects. Analysis names its components (`**Components affected:**`, SPEC plan column `Component`; more than one component = `complex`),
and the breakdown creates every sub-issue in the issues repo with a `Component:` line and a `feat/issue-<n>` branch in each repo
that has one (spec delivery steps 1-2). Implement, review and review fix work in one component (delivery step 3): the target is the
sub-issue's `Component:` line or the simple issue's `Components affected` (`JobSupport.TargetOfIssue`; no component on old data = the only
code component, else the job asks for a new analysis); `AgentSession` undoes every edit outside it (scope guard); the PR is opened in the
component's repo and links the issue with `Issue: owner/repo#n` (never `Closes`); the runner closes issues after the merge
(`SequenceSubIssuesJob`, `FinishIssueJob`). The spec's four delivery steps are built (step 4: claims, README layouts and migration note, `onboarding/PROJECT.md`).
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

- Config: `Configuration/ConfigResolver` reads `repos.yml` strictly into the raw `RunnerFile` types and merges them into
  `ProjectConfig` / `ComponentConfig` / `ProjectSettings`; `RunnerConfig.FindProject(repo)` is the lookup. Nothing outside
  `Configuration/` sees the raw types. A job that needs "the" code component but is not told which uses
  `JobSupport.TargetComponent` (exactly one code component, else it fails and asks for the analysis again).
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

- **Project workspace.** `Infrastructure/Workspace.PrepareAsync` clones or fetches every distinct repo of the project to
  `work/<project>/<repo-name>/` (target repo at the request's start point, the others detached at `origin/<default>`), deletes the
  runner files of the last job from the project root, copies the templates to `<root>/.ai-workflow/templates/` and writes
  `components.md` (`Core/ComponentsDoc`). Runner files live in the project root, never in a clone (each clone also excludes them).
  Copilot always starts in the project root; build/test/lint run in the component folder (`commands.run_in`). The `.git/config`
  guard in `AgentSession` covers every clone.
- **Attachments.** Files people link from human text (`/_attached/<id>` on the GitBucket host; the JSON has no attachment list) are
  downloaded by `Jobs/AttachmentFetcher` right after `IWorkspace.PrepareAsync` (the next prepare removes them) into
  `<project root>/.ai-workflow/attachments/`, and `AttachmentReport` appends an "Attachments" section to context.md / plan.md. Analyze, review,
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
- **Scope guard** (`AgentSession.GuardScopeAsync`, so implement and fix both get it). After every agent round and again after every
  run of the checks (build/test/lint write files too, and the commit stages everything), each time after the `.git/config` guard: every clone of the project is asked for its changed paths (`IGitClient.ChangedPathsAsync`: tracked
  changes against HEAD plus untracked files, ignored files left out). `Core/ScopeRules` decides what goes: every change in a non-target
  repo, in the target repo everything outside the writable component folders, and everything matching `never_edit` (gitignore-like,
  case-insensitive, against the path from the repo root). `IGitClient.RestorePathsAsync` restores tracked paths from HEAD and deletes
  untracked ones (never outside the clone). The list (`AgentResult.Undone`) goes into the PR body or fix reply ("Changes undone by
  ai-runner") and into `fix.md` when a fix round happens anyway; an undo alone starts no fix round. Size limits count the target clone only,
  after the guard. A review fix may change one component (the linked issue's `Component:` line, else its `Components affected` inside the PR's
  repo); with no link every code component of the PR's repo is writable (`AgentRunContext.Writable`).
- **Closing after merge.** GitBucket cannot close an issue of another repo, so the runner does it, from GitBucket data on every poll (a runner that
  was off during a merge catches up). `SequenceSubIssuesJob` reads the PRs in the repo of each sub-issue's component (`SubIssueRef`,
  `SequenceInput`); a sub-issue whose PR `ai/issue-<p>-<o>` is merged into `feat/issue-<p>` gets the PR link as a comment, loses
  `ai:implementing` and is closed, then the next one starts (one global order across repos). After the last one: one final PR per repo that
  has sub-issues (`feat/issue-<p>` into its default branch, body ends with `Issue: owner/repo#<p>`), one parent comment with the
  `<!-- FINAL_PRS -->` tag lists them, and when all are merged the parent is closed. `JobType.FinishIssue` (planned as a routine decision for an
  open non-sub-issue with `ai:implementing` and without `ai-implement`) does the same for a simple issue: the PR (head `ai/issue-<n>`) is looked
  up in the target component's repo; merged = comment + close; closed without merge = comment + drop `ai:implementing`; open or none = quiet.
  A sub-issue never loops: a too-big implement run adds `ai:failed` to it (sequencer waits on `Failed`), and a sub-issue whose PR was closed
  without a merge (`SubIssueSequencer.ClosedWithoutMerge`) gets a comment, loses `ai:implementing` and gets `ai:failed` (a human removes it to retry).
  A ready complex analysis must carry a readable breakdown (`AnalyzeIssueJob` checks it like the SPEC); if it still cannot be read when
  `ai-breakdown` runs, `BreakdownIssueJob` explains, removes `ai-breakdown` and asks for a new analysis (no `ai:failed`, so `@ai` works again).
- **Claims** (`claim_enabled` per project, `runner_name` global; rules in `Core/Claims`, GitBucket calls in `Jobs/ClaimService`). `JobWorker`
  claims the issue or PR before a normal job; a routine job (`JobInfo.Routine`: `SequenceSubIssues`, `FinishIssue`) claims through
  `JobRun.ClaimAsync` just before its first write, so a poll that finds nothing writes nothing. A claim = the `ai:claimed` label + ONE
  comment edited in place, tag `<!-- AI_CLAIM runner=X state=claimed|released|withdrawn at=ISO [until=ISO] -->` (`at` is the claim time, `until` the deadline of
  the claiming job, `ClaimWindow`; only bot comments count, so a human cannot fake a claim). After writing, the runner reads again: the oldest claim comment is the claim;
  a newer duplicate of this runner is edited to `withdrawn` (never deleted). A fresh claim of another runner ends the job as
  `NotNeeded(Quiet)`; a claim is taken over once its `until` and 30 minutes have passed (progress comments like "Copilot started" do not count); a
  claim without `until` (older runner) after 30 minutes with no non-claim bot comment after it. The worker releases (label off,
  comment `released`) when the job ends, success or failure, but not on shutdown (the same name takes it back on the next start).
- **Linked issue of a PR** (`Core/IssueLinks.Find`): `Issue: owner/repo#n` line (the last one), any `owner/repo#n`, `close/fix/resolve #n`,
  the first `#n` (the last two in the PR's repo). A repo outside the project is ignored. The issue is read from its own repo
  (`JobSupport.LinkedIssueAsync`, which also returns the components it names).
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
  vocabularies, `Components affected` names (code components only; `unknown` only while questions are open), the SPEC technical plan
  `| Step | Component | File | Change |` (`SpecParser`), the 6-column breakdown table
  `| # | Component | Title | Scope | Touches | Depends on |` + `Shared contract:`, `[developer]` title prefix).
  `AnalysisParser`, `SpecParser` and `BreakdownParser` take the project's code component names; a name outside them fails the job.
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


## Agent skills

### Issue tracker

Issues live in GitBucket (`ai-bot/ai-workflow`, GitHub-compatible API, no `gh`); agents draft issue text and the user posts it. See `docs/agents/issue-tracker.md`.

### Triage labels

Default five-role vocabulary (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`). See `docs/agents/triage-labels.md`.

### Domain docs

Single-context; the glossary is `CONTEXT.md`. See `docs/agents/domain.md`.
