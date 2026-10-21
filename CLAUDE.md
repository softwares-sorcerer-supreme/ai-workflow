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
Each new head gets one review, up to `limits.max_reviews` (default 3, fix commits count too): the first reads the whole PR,
a later one only `reviewedSha..head` when that commit is an ancestor (else full again), cut to the PR's files (file list and
`diff.patch`, `Core/DiffFiles`, so a merge of the base branch is left out), and re-checks the previous findings
(`### Previous findings`, `P<n>` fixed / still open). The label `ai-review-full` asks for one more full review outside the
limit; the job removes it and stores `full_sha` first. The history lives in `review_state` (`reviewed_sha`, `review_count`,
`full_sha`) and in a runner tag after the REVIEW block (`Core/ReviewHistory`, `<!-- ai-runner:review sha=.. n=.. -->`).
Review fix (workflow `fix`, `FixReviewJob`): a new `@ai` comment on a PR in that same scope (whatever `review.scope` says) makes
Copilot change the PR's branch; it is planned before the review, and the pushed commit is then reviewed as usual.

## Commands

```powershell
dotnet run --project src/AiRunner                       # the loop; with no token: setup mode (see below)
dotnet run --project src/AiRunner -- --once --dry-run   # poll once, print planned jobs, change nothing
dotnet run --project src/AiRunner -- --once             # poll once, run queued jobs, exit
dotnet run --project src/AiRunner -- --setup-labels     # create the workflow labels in every repo
dotnet run --project src/AiRunner -- --copilot-test     # start copilot as a child process
dotnet run --project src/AiRunner -- --attachment-test <url>   # download one _attached file, show the answer (no Copilot)
dotnet run --project src/AiRunner -- --close-test owner/repo#n # close one issue through the web form (no Copilot; it closes it)
dotnet run --project src/AiRunner -- --openapi <abs path>      # write the dashboard API's OpenAPI document (no config, no token)
./build-dashboard.ps1                                           # build the dashboard UI into src/AiRunner/wwwroot (see Dashboard)
```

- Config: `Configuration/ConfigResolver` reads `repos.yml` strictly into the raw `RunnerFile` types and merges them into
  `ProjectConfig` / `ComponentConfig` / `ProjectSettings` (a component's `instructions` list, default `[AGENTS.md]`, is checked there: no `..`, no absolute path); `RunnerConfig.FindProject(repo)` is the lookup. Nothing outside
  `Configuration/` sees the raw types. `models:` takes only workflows that run Copilot (`Workflows.WithModel`; `models.breakdown`
  is an error: the breakdown table comes from the analysis). A job that needs "the" code component but is not told which uses
  `JobSupport.TargetComponent` (exactly one code component, else it fails and asks for the analysis again).
- Tests need no network: GitBucket, Copilot and processes are faked. `GitWorkspaceTests` uses real `git` against a `file://` bare repo.
- Do not run `--copilot-test`, `--once` (without `--dry-run`), `--attachment-test`, `--close-test` or the loop yourself: they spend Copilot premium requests
  and/or touch the real GitBucket. They also need the token (`dotnet user-secrets set GitBucket:Token ... --project src/AiRunner` or env `GITBUCKET_TOKEN`)
  and VPN; leave these to the user.
- **Setup mode**: the loop with no token (no user secret `GitBucket:Token`, no `GITBUCKET_TOKEN` / `GitBucket__Token`)
  does not stop: it starts the dashboard (even when disabled; still 127.0.0.1), logs "No GitBucket token yet: open
  http://localhost:<port>/setup", and the poller and worker wait (`RunnerCredentials.WaitForTokenAsync`: no code-host
  call, no job) until the Setup page saves one. Every other mode stops with `RunMode.NoTokenMessage`.
- **Secrets** live in `Infrastructure/RunnerCredentials` (token, git secret, web password, version), filled by
  `CredentialSources` (user secret, else environment; web password: user secret, else `code_host.web_password`) from
  `UserSecretsStore` (the `UserSecretsId` file; a write keeps the other keys, flattens like `dotnet user-secrets`, temp
  file + move). No client holds the token: `GitBucketCodeHost` sets `Authorization` per request, `GitBucketGitRemote`
  reads the git secret per git call, `GitBucketWeb` signs in again when `Version` changed. `Hosting/RunnerSetup`
  (`IRunnerSetup`, shared with the web app) checks a new secret through `ICredentialCheck` on throwaway clients, saves
  it only when accepted, swaps it in, sets `RunnerIdentity.Login` and wakes the poller. No answer, log line or
  exception message carries a secret (`SetupApiTests` checks it; `RunnerCredentials.Redact` for quoted server text).
- `repos.yml` is found in the current folder or a parent (or `--config <path>`); `paths` in it are relative to its folder.
  The code host is the global `code_host: { kind: gitbucket, base_url, web_password }` block; the old `git_bucket:` block
  (no `kind`) still means `gitbucket`; both at once, a missing or an unknown `kind` stop the start (`ConfigResolver`).

## Architecture: Poll -> Plan -> Queue -> Job

One project, folder = namespace, dependency direction `Hosting -> Jobs -> Infrastructure + Core`:

- `Core/` — pure logic only: no I/O, no DI, no async. All decisions (`JobPlanner`, `SubIssueSequencer`) and all text parsing
  (`AnalysisParser`, `BreakdownParser`, `ReviewParser`, `SubIssueBody`, `Markers`, `MarkdownSections`) and naming (`Labels`, `Branches`,
  `BotComments`) go here. May depend only on the `Configuration/` POCOs.
- `Infrastructure/` — every side effect behind a small interface (`ICodeHost`, `ICopilotRunner`, `IGitClient`, `IProcessRunner`,
  `IWorkspace`, `IStateStore`, `ICostLog`). No business decisions; a code-host/CLI quirk is fixed in exactly one class.
  `Infrastructure/CodeHost/` is the code-host provider (ADR 0004): `ICodeHost` (issues, comments, labels, PRs, close,
  attachments, `FindAttachments`), `IGitRemote` (clone URL, git auth header, PR head refspec; `GitClient` and `Workspace` read it) and
  `CodeHostCapabilities` (what a job must know about the host, named after the job's need: `KeepsLabelsOnCreate`).
  `CodeHostRegistration` picks the provider from `code_host.kind`; GitBucket (`GitBucketCodeHost`, `GitBucketWeb`,
  `GitBucketGitRemote`) is the only one, and no class outside the folder knows its API. Failures are `CodeHostException`.
  The host-neutral models are `Core/CodeHostModels.cs` (`HostIssue`, `HostPull`, `HostComment`, `HostUser`, `HostLabel`,
  `HostRef`, `HostRepo`); tests use `FakeCodeHost` (`JobTestKit.CodeHost`).
- `Jobs/` — one `IJob` per workflow step. A job orchestrates: load fresh data, ask Core, call Infrastructure. Expected failures throw `JobFailedException`.
- `Hosting/` — `Poller` (GitBucket -> `JobPlanner` -> `JobQueue`), `JobQueue` (SQLite-backed, one pending job per key), `JobWorker`
  (one job at a time, timeout, failure comment + `ai:failed`, cost row), `RunnerStatus` (in-memory health the poller and worker
  write), `RunnerCommands` (the dashboard's runtime controls: wake the poller, pause the worker, cancel the running job),
  `DashboardHost`, `RunMode` (command-line flags). Scheduling and lifetime only.
- `Web/` — the dashboard's HTTP API (`DashboardApp`, `MapDashboardApi`, `WriteGuard`, response records). May use `Core`,
  `Configuration`, Infrastructure read interfaces, `Hosting/RunnerStatus`, `Hosting/RunnerCommands` and
  `Hosting/IRunnerSetup` (`Web/SetupApi`) and `Hosting/IRunnerActions` (`Web/ActionsApi`); `Core/` and `Jobs/` never reference it.
- `Program.cs` — DI registration and command-line modes only.

Key mechanics that span several files:

- **Project workspace.** `Infrastructure/Workspace.PrepareAsync` clones or fetches every distinct repo of the project to
  `work/<project>/<repo-name>/` (target repo at the request's start point, the others detached at `origin/<default>`, or at `origin/<OthersAt>` when `WorkspaceRequest.OthersAt` names a branch that exists there: a sub-issue job, its review and its review fix read the other repos at the parent's `feat/issue-<p>` (`Branches.FeatureBranchOf`), and `components.md` says per repo where it is checked out), deletes the
  runner files of the last job from the project root, copies the templates to `<root>/.ai-workflow/templates/` and writes
  `components.md` (`Core/ComponentsDoc`) and `AGENTS.md` (`Core/InstructionsDoc`: PROJECT.md, the `instructions` files of the
  components the job works on in full, the first file of the others; Copilot loads it from its cwd, so the repositories' rules
  reach the agent and the `code-reviewer` subagent). Runner files live in the project root, never in a clone (each clone also
  excludes them, except `AGENTS.md`: a repo's own AGENTS.md is a real file the agent may edit).
  Copilot always starts in the project root; build/test/lint run in the component folder (`commands.run_in`). The `.git/config`
  guard in `AgentSession` covers every clone.
- **Attachments.** Files people link from human text (`/_attached/<id>` on the GitBucket host; the JSON has no attachment list) are
  downloaded by `Jobs/AttachmentFetcher` right after `IWorkspace.PrepareAsync` (the next prepare removes them) into
  `<project root>/.ai-workflow/attachments/`, and `AttachmentReport` appends an "Attachments" section to context.md / plan.md. Analyze, review,
  implement and review fix all use it; bot comments are skipped. `GitBucketCodeHost.DownloadAttachmentAsync` is the only code that
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
  that changed no code keeps the previous checks. While checks fail, a round that ends like the one before (`ImplementGate.Fingerprint`: same
  failing checks with the same output, times and durations blanked, same output-file problems) stops the rounds
  (`ImplementStep.NoProgress`, the round line says "no progress"). Only a round-1 Copilot failure fails the job. Logs per round:
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
  vocabularies (review severity: `bug | risk | convention | style`; a `convention` finding is a difference from the pattern file or the neighbours), `Components affected` names (code components only; `unknown` only while questions are open), the SPEC technical plan
  `| Step | Component | File | Pattern from | Change |` (`SpecParser`; `Pattern from` = the existing file to copy, `-` only when none exists), the SPEC contract tables `### API changes` (7 cells: Method, Path, Query / paging,
  Request, Response, Errors, Auth) and `### UI changes` (5 cells: Route, Page / component, Copy from, Shared components, States), each a table or `None`
  (`SpecParser.Check`; the **contract**, binding for every sub-issue: `ContextBuilder.PlanFromSubIssue` copies the parent's whole SPEC into the
  plan of a sub-issue and then leaves out the body's own `Shared contract` summary, and `JobSupport.LinkedIssueAsync` does the same for its PR), the 6-column breakdown table
  `| # | Component | Title | Scope | Touches | Depends on |` + `Shared contract:`, `[developer]` title prefix).
  `AnalysisParser`, `SpecParser` and `BreakdownParser` take the project's code component names; a name outside them fails the job.
  The analysis `### Questions` section is free text for the runner (no parser reads it): three plain-line groups (Behaviour, Technical, Other),
  one global `Q<n>` numbering, no cap; the analysis context.md shows only the newest ANALYSIS and SPEC in full, older ones are
  one-line notes (`ContextBuilder.ForIssue`), so open questions must be carried into each new analysis. UI work is guided by the `ui-checklist` skill and the `## UI conventions` section of a repo's AGENTS.md
  (a `convention` finding in the review); no runner logic depends on them.
  Changing a template means changing its parser, the agent file in `agents/`, and `tests/AiRunner.Tests/Samples.cs` together.

## Dashboard

Local status page and control panel of one runner (`docs/specs/dashboard.md`, `docs/specs/dashboard-control.md` (built up to
slice 3), `docs/adr/0001`, `0002`, `0004`). `repos.yml` global block
`dashboard: { enabled: false, port: 5180 }`; only the polling loop (not `--once`, `--dry-run` or the other modes) starts
`Hosting/DashboardHost` (in setup mode even when disabled), which builds its own small web app (`Web/DashboardApp`: empty builder, no appsettings/env/secrets, so no
token; Kestrel on `127.0.0.1` only; host filter `localhost;127.0.0.1`; no CORS, no auth). A taken port
is one warning, never a stopped runner. The API never calls GitBucket. Every answer is computed in C# (`Core/RunnerHealth`,
`Core/IssueStage`, later the waiting-on rules) and sent as a stable code plus a label.

- **Writes** are a security boundary: any page the user opens can send a request to `127.0.0.1`. Reads are GET; a write
  route is mapped in the `writes` group of `MapDashboardApi` (metadata `WriteRoute`), and any other method or route is 405.
  `DashboardApp.GuardWritesAsync` is the one method rule (405) and runs `Web/WriteGuard` after routing and before parameter
  binding (so not as an endpoint filter): header `X-AiRunner-Request: 1`, a JSON `Content-Type`, an `Origin` (when sent)
  equal to `http://<AllowedHosts>:<port>`, a loopback caller (the spec's "writes only while Kestrel listens on loopback",
  checked per request); else 403. Order: host filter (400), routing, method rule (405), guard (403). The UI's interceptor adds the header; the dev proxy (`proxy.conf.json`) rewrites `Origin`.
  The web app gets no token and no code-host client: its only ways to act are `Hosting/RunnerCommands` and
  `Hosting/IRunnerSetup`, the same instances the poller and worker use (registered in both containers, like
  `RunnerStatus`). `IRunnerSetup` takes a secret but never returns one. Every new write route gets a guard test.
- **Setup** (`Web/SetupApi`): `GET /api/setup` (sources `user-secrets | environment | none` and
  `user-secrets | repos-yml | none`, login, base URL, secrets file, `canRemove*`; no secret), `PUT`/`DELETE
  /api/setup/token` and `/api/setup/web-password`: 200 `{ message, setup }`, 400 when the check refuses (not accepted,
  not reachable (VPN?), host error), 409 when the value lives in the environment or `repos.yml`. Health code
  `needs-setup` (first rule) plus `isSetupNeeded` for the banner.
- **Runtime controls** (in memory, a restart resumes): `POST /api/runner/poll` wakes the poller (a wake during a poll gives
  one more poll after it); `/pause` and `/resume` hold the worker after its dequeue (the poller keeps polling and queuing;
  health code `paused`, below `stale` and `error`, plus `isPaused`); `POST /api/jobs/{id}/cancel` cancels the running job's timeout source, so it ends
  through the timeout path of `JobWorker` with "Cancelled from the dashboard." (failure comment, `ai:failed` or `OnFailed`,
  claim released, cost row) and is not planned again; 409 when that job is not running (a pending job cannot be cancelled).

- **Actions** (`Core/IssueActions`, `Hosting/RunnerActions`, `Web/ActionsApi`): `actions` (code, label, `needsText`) on every issue row
  and PR; `POST /api/issues/{owner}/{repo}/{n}/actions/{code}` and `POST /api/pulls/...`, body `{ text }` for answer / ask / fix. The rules
  take the same inputs as `WaitingOn` (+ the project's workflows, trigger keyword and the PR's labels / author / draft in `pull_snapshot`). `RunnerActions`
  (shared with the web app through `IRunnerActions`, like `IRunnerSetup`; the web app still has no code-host client) reads the item fresh
  from the code host, checks the action is still offered (else 409), writes through `ICodeHost` (a label, or `AddPersonCommentAsync`: a
  comment without the bot marker, so the planner takes it as a person's `@ai`; it ends with `_(sent from the ai-runner dashboard on <runner_name>)_`),
  then wakes the poller. It never inserts into the queue. Unknown project/action 404, missing text 400, code host failure 502.

- **Workspaces** (`Infrastructure/WorkspaceInspector`, `Hosting/RunnerWorkspaces`, `Web/WorkspacesApi`): `GET /api/workspaces` lists every
  clone of every project (`work/<project>/<repo-name>/`) from local git only (`IGitClient.HeadAsync`, `ChangedPathsAsync`, `.git/FETCH_HEAD`,
  never the network, never the lock) plus the orphan folders of `work/`; size comes from `DirectorySizes` (background walk, `null` until
  done). Warnings are `Core/WorkspaceChecks` (`agent-override`, `instructions-missing`, `has-changes`; changes are not read while a job holds
  the project). Actions: `POST /api/workspaces/{project}/{folder}/fetch|reclone` (`{folder}` = the clone folder name, `ProjectConfig.FolderOf`)
  and `DELETE /api/workspaces/orphans/{folder}`. `Infrastructure/WorkspaceLocks`: `Workspace.PrepareAsync` takes the project for the job (waits
  only for a running action), `JobWorker` releases it in its `finally`; an action only `TryHoldForAction`s and answers 409 when held. Orphan
  delete is a security boundary: `Core/OrphanRules` (plain name only, no dot names, never a project, any case), then the resolved path must be a
  direct child of `work/` and not a link. `IRunnerWorkspaces` is what the web app gets (no git client there).

- **Snapshot** (`issue_snapshot`, `pull_snapshot`; safe to delete like the rest of `ai-runner.db`): `Hosting/SnapshotRecorder` saves what
  `Poller` already lists (issues in scope, open PRs of every code repo, also when review and fix are off), reads once more each row that
  is still `open` but no longer listed (merged or closed since the last poll; `RefreshVanishedAsync`, no call when nothing vanished) and, on
  the first and every 10th poll, keeps the closed issues and runner PRs the dashboard still needs (`Core/SnapshotRules`; a closed sub-issue
  stays while its parent is kept). It never throws and never touches
  fingerprints, decisions or the queue; `--dry-run` writes none. `Core/IssueStage` finds "its PR" through `IssuePulls` /
  `SubIssueSequencer.SlotPulls` / `FinalPull`, the same helpers `FinishIssueJob` and the sequencer use.

- **Cost.** `JobOutcome.CopilotCalls` (analyze/review 1, implement/fix `Rounds.Count`, breakdown and routine jobs 0, a failed job
  null) is written by `JobWorker` to `cost_log.copilot_calls`, `jobs.copilot_calls` and `cost.csv`; old rows are
  null and show as "-". `Core/CostReport` groups by local day, project or issue (a sub-issue rolls up into its parent, a review/fix job
  into the issue its PR links); `GET /api/cost` only serves it.
  Copilot bills AI credits by tokens: `JobSupport.RunCopilotAsync` asks every run for `<logName>-usage.json` in the job's log folder
  (`--usage-output-file`, only when `copilot --help` lists it), and `JobWorker` adds up all of them (`JobRun.ReadUsage`,
  `Core/CopilotUsage`; failed jobs too) into `cost_log` / `cost.csv` columns `nano_aiu`, `input_tokens`, `cache_read_tokens`,
  `cache_write_tokens`, `output_tokens` (null without usage). What each agent spent on each model in each run (`agentMetrics`,
  else `modelMetrics` as agent `main`) goes to `cost_usage_part` (`UsagePart`, not in the CSV); `GET /api/cost?by=model` groups it
  per model and agent, and every cost row carries billed (`nanoAiu`, the CLI's raw value) and the four token counts.

- **Job progress** (`Core/JobSteps`, `Infrastructure/JobStepTracker`, table `job_step`): a log like `jobs`, safe to delete with the
  database; a step write error is a warning, never a failed job (`JobStepTracker` catches it). `JobRun.Step(code, label)` opens a
  step and closes the one before it as ok; `EndStep(outcome, detail)` closes it early (a failed check); `JobWorker` closes the last one
  (`ok`, or `failed` with the failure message) and sets the tracker, `RunnerStatus.JobStep` keeps the current one for the overview. Jobs
  and `AgentSession` call `Step` where they already log (codes in `JobSteps`); `GET /api/jobs/{id}` and the overview's running job carry
  the steps (`JobStepResponse`: state `running | ok | failed | interrupted`, from `JobSteps.StateOf`). Copilot output is also appended
  line by line to `<logName>-live.txt` while it runs (`ProcessSpec.LiveOutputFile`, `CopilotRequest.LiveFile`; the `-stdout` / `-stderr`
  files are saved as before), and `GET /api/jobs/{id}/files/{name}?offset=` returns text from a byte offset (whole characters) plus
  `nextOffset` through the same `LogViewer` path rules.
- UI: Angular + Angular Material in `src/AiRunner.Dashboard/`; its rules for every screen are in `src/AiRunner.Dashboard/UI.md`
  (the `dashboard-ui` subagent follows it). **No workflow logic in the UI**: it never reads labels or computes a stage, a
  waiting-on action, a health state or a total.
- The TypeScript client in `src/AiRunner.Dashboard/src/app/api/` is generated from the OpenAPI document and committed with
  `src/AiRunner.Dashboard/openapi.json`. After any API change run `./build-dashboard.ps1` (or `--openapi` + `npm run generate:api`)
  and commit the regenerated files with the C# change.
- npm commands (in `src/AiRunner.Dashboard/`): `npm start` (dev server, `/api` proxied to `127.0.0.1:5180`), `npm test` (unit tests
  once), `npm run build`, `npm run generate:api`. Node must match `engines` in its `package.json`.
- `dotnet build` and `dotnet test` never need Node. `build-dashboard.ps1` copies the build to `src/AiRunner/wwwroot/` (not committed);
  without it the runner serves a "dashboard not built" page.

## GitBucket and security constraints

- GitBucket returns HTTP 500 on non-ASCII text: all outgoing text goes through `AsciiSanitizer` (done in `GitBucketCodeHost`); keep templates ASCII.
- `since` on issue lists is ignored; comment `DELETE` fails (use `PATCH`, never delete); `/pulls/{n}/files` does not exist (use `git diff` on the clone).
  Lists are paged with `per_page=100&page=n` until a page brings nothing new.
  `/api/v3` cannot edit an issue (`PATCH issues/{n}` = 404), so `GitBucketCodeHost.CloseIssueAsync` signs in on the web side as the token's
  user (`code_host.web_password`, repos.yml only; `RunnerConfig.LoadFile` returns it in `ConfigFile` beside the config,
  never inside it, so it cannot reach the dashboard; `GitBucketWeb` owns the cookie session and the sign-in), posts the issue page's form
  `{repo}/issue_comments/state` (`issueId`, `action=close`) and reads the issue back from the API (still open = exception,
  and the next close signs in again). Writes after a close go after it (`JobSupport.CloseDoneAsync`, the parent close),
  so a failed close posts nothing to repeat on the next poll.
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
- Tests mirror the layers (`Core/`, `Jobs/`, `Hosting/`, `Infrastructure/`, `Web/` on a `TestServer`). Job tests use `JobTestKit` (`Fakes/Fakes.cs`, `FakeCodeHost`)
  and build data with `Samples.Issue/Human/Bot` and the filled templates in `Samples.cs`.
- `SQLitePCLRaw.lib.e_sqlite3` is pinned in the csproj to fix NU1903; don't drop it.


## Agent skills

### Issue tracker

Issues live in GitBucket (`ai-bot/ai-workflow`, GitHub-compatible API, no `gh`); agents draft issue text and the user posts it. See `docs/agents/issue-tracker.md`.

### Triage labels

Default five-role vocabulary (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`). See `docs/agents/triage-labels.md`.

### Domain docs

Single-context; the glossary is `CONTEXT.md`. See `docs/agents/domain.md`.
