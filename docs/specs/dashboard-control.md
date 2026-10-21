# SPEC: Dashboard control (act, configure, watch)

Status: draft for review, then ready for an implementing agent, slice by slice (see Delivery order).
Read `CLAUDE.md` first. Its architecture, security and code-style rules apply to every line of this work.
Builds on `docs/specs/dashboard.md` (the read-only v1, built). Decisions: `docs/adr/0001` (dashboard inside the
runner), `0002` (Angular + generated client), `0004` (code-host provider, live settings; supersedes `0003`).

## Problem Statement

The dashboard shows what the runner does, but every change still happens somewhere else. To start an
implementation, retry a failed job or answer the agent's questions, the user goes to GitBucket and adds a label or
writes a comment, then waits up to a poll interval. To change a model, a limit or a command, they edit `repos.yml`
and restart the runner. To change an agent or a skill they edit the file and restart too, because the runner copies
`agents/` and `skills/` to `~/.copilot/` only at startup, and nothing tells them when the copy is stale. They cannot
see the clones under `work/` (which branch, how old, whether a repo's own `.github/agents/` overrides the shared
agent), and while a job runs they only see "round 2", not which step it is in. The model is chosen per project and
workflow only: not per issue, not by complexity, not for fix rounds or subagents. And every line of the runner
assumes GitBucket, which the team may replace.

## Solution

The dashboard becomes the runner's control panel, still local and still one runner:

- A **code-host provider** seam, with GitBucket as its first implementation, so the host can be replaced later.
- **Write endpoints** behind a CSRF guard, and **runtime controls**: poll now, pause and resume, cancel the running job.
- **Actions** on issues and PRs (implement, break down, retry, answer, full review, fix, start the next sub-issue,
  set a model) that are code-host writes the planner then picks up (ADR 0004), never a direct queue insert.
- **Job progress**: the steps of the running job and its log while it runs.
- **Workspaces**: what is under `work/`, with fetch, re-clone and clean-up.
- **Agents and skills**: edit, validate, diff against git, and **sync** to `~/.copilot/` without a restart, with a
  warning when the copy has drifted.
- **Live settings**: every setting of `repos.yml` edited on a Settings screen and applied without a restart.
- **Model selection** per stage, per complexity, per issue and per subagent, with the model each job used shown.

All rules stay in C# `Core`; Angular only shows what the API returns and sends what the user chose.

## User Stories

1. As a runner owner, I want a "poll now" button, so that a change I just made on the code host is picked up at once.
2. As a runner owner, I want to pause and resume the runner, so that it takes no new job while I change things.
3. As a runner owner, I want to cancel the running job, so that a job going the wrong way stops spending premium requests.
4. As a product owner, I want the next action of an issue as a button (implement, break down, retry, start the next sub-issue), so that I do not have to remember label names.
5. As a product owner, I want to answer the agent's questions from the issue page, so that I stay in one place.
6. As a developer, I want to ask for a full review or a review fix of a PR from the dashboard, so that I do not hunt for the PR on the code host.
7. As a runner owner, I want every action to leave a trace on the code host, so that teammates see who asked for what.
8. As a developer, I want to see the steps of the running job (prepare, Copilot round, build, test, lint, push, PR) and its log while it runs, so that I know what it is doing now.
9. As a runner owner, I want to see every clone under `work/` with its branch, last commit, last fetch and size, so that I know what the agent works on.
10. As a runner owner, I want a warning when a product repo has its own `.github/agents/` file that overrides a shared agent, or when an instruction file named in `repos.yml` is missing, so that I am not surprised by agent behaviour.
11. As a runner owner, I want to fetch, re-clone or delete a clone, and remove folders of projects no longer in `repos.yml`, so that I can fix a broken workspace without a terminal.
12. As a runner owner, I want to edit an agent or a skill with validation and see its diff against git, so that I can tune agents safely and roll back.
13. As a runner owner, I want to know when `~/.copilot/` is out of date with `agents/` and `skills/`, and to sync it with one button, so that a change takes effect without a restart.
14. As a runner owner, I want to change any setting on a Settings screen with the same validation as at startup, so that I never restart the runner for a config change.
15. As a runner owner, I want a hand edit of `repos.yml` to be picked up too, so that the file and the screen never disagree.
16. As a runner owner, I want to choose the model per workflow, per complexity and for fix rounds, so that the strong model is used only where it pays.
17. As a developer, I want to set the model of one issue, so that a hard issue gets a stronger model.
18. As a runner owner, I want to choose the models of the subagents (`code-scout`, `code-reviewer`), so that cheap work runs on a cheap model.
19. As a developer, I want to see which model each job used and why, so that I can compare models with the cost screen.
20. As a team lead, I want the runner to talk to the code host through one provider, so that we can move off GitBucket later.

## Implementation Decisions

### 1. Code-host provider (ADR 0004)

- `IGitBucketClient` becomes `ICodeHost` (`Infrastructure/CodeHost/`), `GitBucketClient` becomes `GitBucketCodeHost`
  and stays the only class that knows GitBucket. The methods stay the ones the jobs use today (issues, comments,
  labels, PRs, close, create label, attachments, current user, repo).
- The `Gb*` models in `Core/GitBucketModels.cs` are renamed to host-neutral names (`HostIssue`, `HostPull`,
  `HostComment`, ...) in `Core/CodeHostModels.cs`, same fields. They describe what the workflow needs, not GitHub's
  REST shape: a later GitLab provider maps merge requests, `iid` and notes onto them.
- `CodeHostCapabilities` (record, on `ICodeHost`): what the host can and cannot do, for example `IsAsciiOnly`,
  `CanListPullFiles`, `ClosesIssuesThroughWeb`. Jobs read a capability instead of assuming GitBucket. Today's
  behaviour comes from the GitBucket values.
- The git auth header (`GIT_CONFIG_*` env vars in `GitClient`) comes from the provider, since hosts differ.
- Config: a new global block `code_host: { kind: gitbucket, base_url, web_password }`. The old `git_bucket:` block
  keeps working and means `kind: gitbucket`; both blocks at once stop the start, like any strict-config error.
  An unknown `kind` stops the start and lists the known ones.
- This slice changes no behaviour: every test stays green with renamed fakes (`FakeCodeHost`).

### 2. Writes and runtime controls

- **CSRF guard** (`Web/WriteGuard`, endpoint filter on every non-GET route): the request must carry the header
  `X-AiRunner-Request: 1` and `Content-Type: application/json`, and an `Origin` header, when present, must be the
  dashboard's own origin (`http://localhost:<port>` or `http://127.0.0.1:<port>`); anything else is 403. A
  cross-site form cannot set the header, and a cross-site `fetch` with it needs a preflight the runner never answers
  (no CORS). The host filter stays. The dev proxy (`proxy.conf.json`) rewrites `Origin` to the runner's origin.
- Write routes are allowed only while Kestrel listens on loopback (today always). Routes that are not mapped still
  return 405.
- The web app does not get a token or a code-host client. It gets one service, `Hosting/RunnerCommands`, registered
  as the same instance in both containers (like `RunnerStatus`). `RunnerCommands` runs inside the runner and uses
  the provider; the web endpoints only validate input and call it.
- **Poll now**: `Poller` waits on its `PeriodicTimer` or a wake signal (`RunnerCommands.WakePoller`), whichever
  comes first; a poll already running is not interrupted, and one wake during a poll gives one more poll after it.
- **Pause / resume**: `JobWorker` takes no new job while paused; the poller keeps polling and queuing, so the
  dashboard stays current. In memory only (a restart resumes); the health strip shows "Paused" (new health code).
- **Cancel the running job**: cancels the job's token through the same path as a timeout, with the reason
  "Cancelled from the dashboard": failure comment, `ai:failed` on an issue, `OnFailed` on a PR job (its `@ai`
  comment counts as handled), claim released, cost row written. So a cancelled job is not planned again on the next
  poll; the Retry action starts it again. A pending job cannot be cancelled (the next poll would queue it again);
  pause the runner instead.
- Routes: `POST /api/runner/poll`, `POST /api/runner/pause`, `POST /api/runner/resume`,
  `POST /api/jobs/{id}/cancel` (409 when that job is not running). Each answers `202` with a short message.

### 3. Actions on issues and PRs

- `Core/IssueActions.Available(...)` gives the actions of an issue as code + label + kind, from the same inputs as
  `WaitingOn` (stage, flags, badge, sub-issues, settings). The API sends them in the list row and the detail;
  Angular shows one button per action and never decides which exist.

| Action | Offered when | Code-host write |
|---|---|---|
| `implement` | Ready - simple, no `ai-implement` | add `ai-implement` |
| `breakdown` | Ready - complex, no `ai-breakdown` | add `ai-breakdown` |
| `retry` | `ai:failed` | remove `ai:failed` |
| `answer` | Questions open | comment `<trigger_keyword> <text>` (text required) |
| `ask` | open, not queued or running, analyze workflow on | comment `<trigger_keyword> <text>` (asks for a new analysis round) |
| `start-next-sub-issue` | the `WaitingOn` rule "start the next sub-issue" holds | add `ai-implement` on the next open sub-issue in build order (`SubIssueSequencer`) |
| `set-model` | open | a settings comment (section 8) |

  On a PR (issue detail, PR list): `review-full` (add `ai-review-full`, PR in review scope) and `fix` (comment
  `<trigger_keyword> <text>`, fix workflow on, PR in fix scope).
- `POST /api/issues/{owner}/{repo}/{number}/actions/{code}` and `POST /api/pulls/{owner}/{repo}/{number}/actions/{code}`,
  body `{ "text": "..." }` where needed. `RunnerCommands` reads the issue or PR **fresh from the code host** (not
  the snapshot), checks with `IssueActions` that the action is still offered (409 with the reason if not), writes,
  then wakes the poller. No queue insert: the planner decides, as always.
- Trace: a comment written for an action is a human-style comment by the token's user with one last line
  `_(sent from the ai-runner dashboard on <runner_name>)_`; it is not a bot comment, so `AtAiThread` treats it as a
  normal trigger. Label changes show in the code host's own history. Text goes through `AsciiSanitizer` like every
  outgoing text (in the provider).
- The analysis of an issue with no trigger label is not an action: the snapshot only holds issues the runner
  already polls.

### 4. Job progress

- New table `job_step(job_id, seq, code, label, started_at, ended_at, outcome, detail)`, a log like `jobs`
  (deleting the DB stays safe). `JobRun.Step(code, label)` opens a step and closes the previous one; the job's end
  closes the last. Codes: `claim`, `prepare`, `attachments`, `copilot` (label "Copilot round N"), `check` (label
  "build" / "test" / "lint"), `scope-guard`, `commit`, `push`, `pull-request`, `comment`, `close`.
  `AgentSession` and each job call it at the points where they already log.
- `RunnerStatus.Running` gets the current step. `GET /api/jobs/{id}` and the overview's running job include the steps.
- Live log: Copilot output is only saved when the run ends (`JobSupport.RunCopilotAsync`). `ProcessRunner` gets an
  optional output file it appends to while the process runs (stdout and stderr lines as they come), used for
  Copilot runs as `round-N-copilot-live.txt`. `GET /api/jobs/{id}/files/{name}?offset=` returns the text from a byte
  offset plus the new offset, so the log viewer tails a file with the usual 3 s poll. The path rules of the log
  viewer stay as they are.

### 5. Workspaces

- `GET /api/workspaces`: one entry per project of the config, each with its clones (one per distinct repo,
  `work/<project>/<repo-name>/`), plus **orphans**: folders under `work/` that are no project of the config.
- Per clone, read from local git only (no network): exists or not, components using it, HEAD sha, subject and
  date, branch or "detached at origin/<x>", last fetch (`.git/FETCH_HEAD` time), changed files (should be none
  between jobs), size on disk (computed in the background and cached; `null` until known).
- Warnings, computed in `Core/WorkspaceChecks`: the clone has `.github/agents/<name>.agent.md` for an agent the
  runner installs (the repo's copy wins over the shared one); a file named in a component's `instructions` is
  missing; a clone holds changes between jobs.
- Actions: `POST /api/workspaces/{project}/{repo}/fetch` (git fetch with the usual auth env),
  `POST .../reclone` (the existing "delete one clone, the next job clones again" of `IWorkspace`),
  `DELETE /api/workspaces/orphans/{folder}` (a plain folder name under `work/`, the resolved path must stay inside
  `work/`, never a project of the config).
- A per-project lock in `Workspace`: a job holds it from `PrepareAsync` to its end; a workspace action tries it
  without waiting and answers 409 "a job is using this project" when it is held.
- Screen **Workspaces**: project cards, a table of clones, warnings as inline notes, the actions as buttons
  (re-clone and delete ask for confirmation).

### 6. Agents and skills

- `GET /api/agents`: every file of `agents/*.agent.md` and `skills/**` (the folders `AgentInstaller` copies), each
  with its git state in the runner's own repo (`unchanged`, `modified`, `untracked`, or `unknown` when the folder
  is not in a git repo; read with `git status --porcelain`) and its sync state.
- `GET /api/agents/{path}`: the text, a content hash, and the unified diff against git HEAD (`git diff`), when modified.
- `PUT /api/agents/{path}` with `{ text, baseHash }`: 409 when the file changed since it was read; else
  `Core/AgentFileRules` checks the file and refuses with the reasons: frontmatter is valid YAML; an agent has
  `name` equal to its file name stem, a non-empty `description`, `tools` (when present) a list of strings,
  `model` (when present) in `allowed_models` (when that list is set); a skill's `SKILL.md` has `name` and
  `description`. Writes are atomic (temp file + replace). Only existing files are edited; no create or delete.
- `POST /api/agents/{path}/revert`: restore the file from git HEAD (confirmation in the UI).
- **Drift and sync** (`Core/AgentDrift`): compare the hash of every source file with its copy under
  `~/.copilot/agents` and `~/.copilot/skills`: `in-sync`, `differs`, `missing` (not installed). Files in
  `~/.copilot/` that the repo does not have are listed as `extra` and never deleted (the user may use Copilot for
  other work). The drift is checked at every poll; the health strip shows "Agents out of sync" when any file
  `differs` or is `missing`, so a `git pull` of this repo while the runner runs is noticed.
- `POST /api/agents/sync`: asks `JobWorker` to run `AgentInstaller.Install` between jobs (at once when idle, else
  right after the running job, so one Copilot run never mixes old and new agents). A save on the dashboard asks for
  a sync by itself. The installer also writes the subagent models (section 8).
- `templates/` are shown read-only, with a note: a template change needs its parser, its agent file and
  `Samples.cs` changed together (`CLAUDE.md`), which is a code change, not a dashboard edit.
- Screen **Agents**: file list with git and sync state, an editor (monospace textarea with the validation messages
  under it; no editor library in v1), a diff view, Save, Revert, and a Sync banner when anything is out of sync.

### 7. Live settings (ADR 0004)

- `Configuration/RunnerConfigSource` holds the current `RunnerConfig` and a version. Every consumer gets the config
  from it instead of a `RunnerConfig` captured at startup (32 classes take one in their constructor today). A poll
  reads `Current` once at its start; a job reads it once in `JobWorker` and uses that snapshot to its end (a DI
  scope per job with `RunnerConfig` registered as scoped is the suggested way; jobs are singletons today and become
  scoped). The dashboard reads `Current` per request.
- **Reload**: after a save on the dashboard, and when `repos.yml` changes on disk (its hash is checked at every
  poll). The new file goes through the same strict `ConfigResolver`; if it fails, the old config stays, the health
  strip shows "repos.yml has errors" with the message, and the Settings screen shows it too.
- **Restart-needed keys**: `dashboard.*`, `paths.*`, `code_host.kind`, `code_host.base_url`. They are saved, not
  applied, and shown as "restart needed" until the next start.
- **Writing `repos.yml`**: the runner serializes the raw `RunnerFile` (snake_case, same key order as today's file),
  checks it with `ConfigResolver`, writes a temp file and replaces `repos.yml` atomically, after copying the old file
  to `repos.yml.bak-<UTC timestamp>` (the last 10 are kept). Comments in the file are not kept; the first save
  warns about that. Their text becomes field help (below).
- **Settings schema** (`Configuration/SettingsSchema`, C#): per key its path, type, allowed values, help text,
  default, scope (`global`, `defaults`, `project`, `component`), and flags `restartNeeded`, `secret`. The help text
  is today's comments of `repos.yml`. `GET /api/settings/schema` serves it; the screen is built from it, so a new
  key needs no Angular change.
- `GET /api/settings`: the raw file as JSON plus a hash (secrets replaced by `{ isSet: true|false }`), and per
  project the merged effective values with where each one comes from (`default`, `defaults:`, the project).
  `PUT /api/settings` with `{ file, baseHash }`: 409 when the file changed since it was read (hand edit), 400 with
  the resolver's message when it is invalid, else saved and applied. A secret sent as `null` keeps its value.
- The token stays in user secrets or `GITBUCKET_TOKEN` (never in `repos.yml`, never through the API); the screen
  says whether the running provider has one and how to set it.
- `commands` and `allow_shell` are editable. That is running commands on this machine, acceptable only because the
  dashboard is loopback-only (section 2). Opening it to a network needs a login or a token first (out of scope).
- Screen **Settings**: tabs Global, Defaults, one per project (components inside), forms built from the schema,
  "inherited from defaults" hints with a reset-to-inherited button, a diff of the change before Save, and the
  restart-needed list.

### 8. Model selection

- `models.<workflow>` takes a string as today, or an object:
  `implement: { default: claude-sonnet-5.5, complex: claude-opus-5.5, fix_rounds: claude-sonnet-5.5 }`.
  Keys: `default`, `simple`, `complex` (from the issue's `ai:simple` / `ai:complex`; a sub-issue reads its parent's),
  and `fix_rounds` (implement and fix only: Copilot runs after round 1). Unknown keys stop the start.
- Global `allowed_models: [...]`: when set, every model in `models`, `agent_models`, an agent file and an issue
  override must be in it (startup, settings save, agent save, override action). Empty = no check, as today.
- Global `agent_models: { code-scout: claude-haiku-5.5, code-reviewer: claude-sonnet-5.5 }`: `AgentInstaller` writes
  `model:` into the frontmatter of the installed copy (the source file is unchanged). Global only: `~/.copilot/` is
  one folder for every project.
- **Issue override**: a bot comment with the tag `<!-- AI_SETTINGS models.implement=claude-opus-5.5 -->` (keys
  `models.<workflow>`; the newest such comment wins; an empty value removes the override). Written by the
  `set-model` action (section 3) as a bot comment, so a human cannot fake it, and visible on the issue. A
  sub-issue's PR jobs read the sub-issue, then its parent.
- `Core/ModelResolver.Resolve(input)` gives the model and the reason, first match wins: issue override, then
  `fix_rounds` (fix round), then `simple`/`complex`, then `default`/string, then none (Copilot's default).
  `AgentSession`, `AnalyzeIssueJob` and `ReviewPullRequestJob` call it instead of `ModelFor`.
- `jobs` gets nullable `model` and `model_reason` columns (added at startup when missing), set by the job; a fix
  round with another model notes it in its round line. The Jobs screen, the issue timeline and the cost screen show
  them ("claude-opus-5.5 - issue override").

### API (all under `/api`; writes behind the CSRF guard)

| Route | Does |
|---|---|
| `POST /api/runner/poll`, `/pause`, `/resume` | runtime controls |
| `POST /api/jobs/{id}/cancel` | cancel the running job |
| `GET /api/jobs/{id}` (extended), `GET /api/jobs/{id}/files/{name}?offset=` | steps; log tail |
| `POST /api/issues/{owner}/{repo}/{number}/actions/{code}` | issue action |
| `POST /api/pulls/{owner}/{repo}/{number}/actions/{code}` | PR action |
| `GET /api/workspaces`, `POST .../fetch`, `POST .../reclone`, `DELETE /api/workspaces/orphans/{folder}` | workspaces |
| `GET /api/agents`, `GET/PUT /api/agents/{path}`, `POST /api/agents/{path}/revert`, `POST /api/agents/sync` | agents and skills |
| `GET /api/settings/schema`, `GET /api/settings`, `PUT /api/settings` | live settings |

`/api/issues` rows and `/api/issues/{...}` gain `actions`; `/api/health` gains `paused`, `agentsOutOfSync`,
`configError`.

### Delivery order (one issue and one PR each; run the full test suite after each)

1. **Provider seam**: `ICodeHost`, host-neutral models, capabilities, `code_host:` config. No behaviour change.
2. **Writes + runtime controls**: CSRF guard, `RunnerCommands`, poll now, pause/resume, cancel; buttons on Overview
   and the health strip.
3. **Actions**: `IssueActions`, action routes, buttons on Issues, Issue detail and its PR list, answer/ask/fix text box.
4. **Job progress**: `job_step`, steps in the API, live Copilot log and tail; Overview and Jobs show the steps.
5. **Workspaces**: read-only list and warnings first, then the actions with the per-project lock; Workspaces screen.
6. **Agents and skills**: list, edit, validate, diff, revert, drift, sync; Agents screen; templates read-only.
7. **Live settings**: `RunnerConfigSource` + scoped jobs, reload and file watch, writer with backups, schema,
   Settings screen.
8. **Model selection**: `models` object form, `allowed_models`, `agent_models`, issue override + `set-model`,
   `ModelResolver`, `jobs.model`; shown on Jobs, Issue detail and Cost.

Slices 4 and 5 (first half) do not depend on 2 and can go earlier. Slice 8 depends on 7 (its keys are edited there)
and on 3 (`set-model`).

## Testing Decisions

- Good tests check external behaviour: what an API route answers and writes, what the code host received, what
  the config holds after a reload, what `~/.copilot/` holds after a sync. Not private helpers or call order.
- **Pure Core** as table tests: `IssueActions` (every row of the table, plus "not offered" cases), `ModelResolver`
  (every rule and the order), `AgentDrift`, `AgentFileRules`, `WorkspaceChecks`, the settings tag parser.
- **CSRF guard** on a TestServer: a write without the header, with a foreign `Origin`, or with a form content type is
  403; the right request passes; a GET needs nothing; an unmapped method is still 405; a foreign `Host` is still refused.
- **Actions** with `FakeCodeHost`: the label or comment written, the fresh read before it, 409 when the action is no
  longer offered, the poller woken, nothing queued directly.
- **Runtime controls**: a wake runs one extra poll; pause stops new jobs and not polls; cancel goes through the
  timeout path (failure comment, `ai:failed`, claim released, cost row) and the job is not planned again.
- **Workspaces** with real `git` against `file://` bare repos (like `GitWorkspaceTests`): clone info, override and
  missing-instruction warnings, re-clone, orphan delete refuses `..`, rooted names and project folders, 409 under the lock.
- **Agents**: save refuses bad frontmatter and a stale `baseHash`; revert restores HEAD; drift states; sync waits for
  the running job; installed copy gets `agent_models` while the source stays unchanged. Use a temp folder as the
  Copilot home (the installer takes it as a parameter, never the real one in tests).
- **Live settings**: a save writes a file the resolver reads back to the same config; a backup is made and only 10
  are kept; an invalid save changes nothing; a hand edit is reloaded at the next poll; a broken hand edit keeps the
  old config and reports the error; a running job keeps its snapshot; restart-needed keys are not applied; secrets
  never appear in a GET.
- **Provider slice**: the whole existing suite, unchanged in behaviour.
- **Angular**: unit tests per new screen and for the action buttons, the confirmation dialogs and the log tail,
  as UI.md says. No end-to-end tests.
- Tests need no network and never run Copilot.

## Out of Scope

- A second provider (GitHub, Gitea, GitLab): only the seam and GitBucket.
- Login, roles, LAN access, a team view across runners.
- Creating or deleting agent, skill or template files; editing templates; committing or pushing this repo from the
  dashboard (the user commits).
- Editing product-repo files (`AGENTS.md`, `PROJECT.md`, `.github/agents/`) from the dashboard; a later "propose a
  change" action would open a PR through the provider.
- Starting the analysis of an issue the runner does not poll yet.
- Reordering the queue, priorities, cancelling a pending job.
- A model scorecard (outcome per model: fix rounds, green at round 1, review findings, merged); the data from
  slice 8 and the cost tables makes it possible later.
- Notifications (toast, chat webhook).
- Websockets or server-sent events (polling stays).

## Further Notes

- `CLAUDE.md` calls the dashboard "read-only": update its Dashboard section in slice 2, and the sections on
  `GitBucketClient`, `IGitBucketClient` and the `Gb*` models in slice 1.
- New terms go to `CONTEXT.md` in the slice that brings them: **Code host**, **Action**, **Sync**, **Drift**,
  **Model override**.
- `UI.md` drops "read-only" in slice 2 and gains the patterns for buttons that write: a confirmation dialog for
  anything destructive, a busy state on the button, the server's message as a snackbar, never an optimistic update
  (the next poll shows the real state).
- Each slice regenerates the OpenAPI document and the client (`./build-dashboard.ps1`) and commits them with the C#.
- Do not run `--copilot-test`, `--once` without `--dry-run`, `--attachment-test`, `--close-test` or the loop; the user
  tries the dashboard by running the loop themselves.
