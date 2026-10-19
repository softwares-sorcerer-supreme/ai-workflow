# SPEC: Dashboard (read-only status of one runner)

Status: ready for an implementing agent, slice by slice (see Delivery order).
Read `CLAUDE.md` first. Its architecture, security and code-style rules apply to every line of this work.
Decisions behind this spec: `docs/adr/0001` (dashboard inside the runner), `0002` (Angular + generated client),
`0003` (future control goes through GitBucket). Terms: **Stage**, **Waiting on**, **Snapshot**, **Health** in `CONTEXT.md`.

## Problem Statement

Many issues and PRs on GitBucket are moved forward by the runner, but GitBucket only shows labels and long comment
threads. From there it is hard to see what the agent is doing right now, what is queued, which issues wait for a
human (answer questions, approve a breakdown, merge a PR), which ones failed, which ones are done, and what it all
costs. When the VPN or the token breaks, "idle" and "broken" look the same.

## Solution

A local web dashboard served by the runner itself, read-only in v1. It shows one runner (the one it runs in):
its health, the running job, the queue, job history with logs, cost, and the workflow **stage** of every issue the
runner polls. All rules (stage, what a human must do next, cost totals) are computed in C# `Core`; the Angular app
only displays what the API returns. The dashboard never calls GitBucket: the poller saves a **snapshot** of what it
already reads.

Later phases (not in this spec): a team view across runners, and control actions (start a job, edit settings).
`docs/adr/0003` fixes their direction so v1 does not block them.

## User Stories

1. As a runner owner, I want to see at a glance whether the runner is healthy (last poll, last error, busy or idle), so that I know if "nothing happens" means idle or broken.
2. As a runner owner, I want to see the running job with its issue or PR, type, round and elapsed time, so that I know what the agent is doing now.
3. As a runner owner, I want to see the queue in order, so that I know what comes next.
4. As a product owner, I want a "waiting on you" list with the action each item needs, so that I never miss an issue that is blocked on me.
5. As a product owner, I want every polled issue with its stage, open/closed state and flags (failed, claimed, on GitHub), filterable by project, component and stage, so that I can follow the pipeline.
6. As a product owner, I want sub-issues nested under their parent with n/m progress, so that a split issue reads as one piece of work.
7. As a developer, I want an issue page with its job timeline, sub-issues, PRs and analysis summary, so that I can follow one issue end to end.
8. As a developer, I want job history with outcome, duration and Copilot calls, and to read the job's log files in the browser, so that I can debug a failed job without opening folders.
9. As a runner owner, I want Copilot calls and job time per day, project and issue, so that I can see where premium requests go.
10. As a runner owner, I want to turn the dashboard on and choose its port in `repos.yml`, so that it does not surprise teammates.
11. As a runner owner, I want the runner to keep working when the dashboard port is taken, so that the dashboard can never stop the real work.
12. As a runner owner, I want the dashboard reachable only from my own machine, so that my code, diffs and logs do not leak.

## Implementation Decisions

### Config

New global-only block in `repos.yml` (strict like every other key; not in `defaults`, not per project):

```yaml
dashboard:
  enabled: false      # default false: opt-in
  port: 5180
```

### Hosting (`docs/adr/0001`)

- The runner project keeps `Microsoft.NET.Sdk.Worker` and adds `<FrameworkReference Include="Microsoft.AspNetCore.App" />`.
- A hosted service `Hosting/DashboardHost` builds its own small `WebApplication` and starts it. It is registered only in
  loop mode with `dashboard.enabled: true`; `--once`, `--dry-run` and the other CLI modes never start it.
- The runner's singletons the API reads (`RunnerConfig`, `IStateStore`, `JobQueue`, `RunnerStatus`, the logs path) are
  registered in the web app as the same instances. No second GitBucket client, no token in the web app.
- Kestrel listens on `127.0.0.1:<port>` only. Host filtering allows `localhost` and `127.0.0.1` only (DNS-rebinding
  guard). No CORS headers. No auth in v1.
- If Kestrel cannot bind (port taken), `DashboardHost` logs one warning with the port and the config key and returns;
  the poller and worker keep running.
- Endpoint code lives in a new folder `Web/` (namespace `AiRunner.Web`): `MapDashboardApi(this IEndpointRouteBuilder)`
  plus small response records. `Web/` may use `Core`, `Infrastructure` read interfaces and `Hosting/RunnerStatus`;
  `Jobs/` and `Core/` never reference `Web/`.
- OpenAPI document from ASP.NET Core's built-in OpenAPI support at `/openapi/v1.json`.
- Static files: if `wwwroot/` next to the runner holds `index.html`, it is served with SPA fallback for unknown
  non-`/api` routes. If not, `/` returns a short HTML page: "dashboard not built, run `./build-dashboard.ps1`".

### Runner status (in memory)

`Hosting/RunnerStatus` (singleton, thread-safe, no I/O) holds:

- started at, runner name, config path, Copilot CLI version (read once at startup with `copilot --version`; that
  costs no premium request; "unknown" when it fails);
- per poll: start and end time, and the last error per project (the messages `Poller` already logs);
- worker: idle, or the running job (job id, type, repo, number, started at, current round when the job is an agent session).

`Poller`, `JobWorker` and `AgentSession` write to it. Nothing in it is needed for correctness; a restart clears it.

### Snapshot (`docs/adr/0001`: the dashboard makes no GitBucket calls)

- New SQLite tables, a cache only (deleting the DB stays safe; the next polls rebuild them):
  - `issue_snapshot(repo, number, project, title, state, labels, assignees, parent, component, html_url, created_at, updated_at, closed_at, seen_at)`
  - `pull_snapshot(repo, number, project, title, state, merged, head_ref, base_ref, head_sha, linked_issue, html_url, updated_at, seen_at)`
- Issues: every poll writes the issues the poller already lists for each project (trigger label or assigned to the
  runner user, `Poller.PollIssuesAsync`). `parent` and `component` come from the issue body (`SubIssueBody`).
- Open PRs: every poll lists the open PRs of each code repo, also when `review` and `fix` are both off (today the poller
  then skips PRs). `linked_issue` comes from `IssueLinks.Find`.
- Slow refresh, every 10th poll and on the first poll after start: closed issues with the trigger label
  (`ListClosedIssuesAsync`) and closed PRs of each code repo. Keep a closed issue when it was updated within
  `log_retention_days` or its parent is still open (so n/m stays right); keep a closed PR when its head is a runner
  branch (`ai/issue-*`, `feat/issue-*`, `ai/pr-*`) and its linked issue is in the snapshot.
- An open issue that is no longer listed and not found as closed is deleted from the snapshot at the slow refresh.
- Writing the snapshot never changes a planner decision, a fingerprint or the queue. A snapshot write error is logged
  and the poll goes on.

### Stage (pure `Core`, single source of truth)

`Core/IssueStage` computes, from one issue snapshot row, its PRs, its sub-issues and the project's settings:

| Stage | Rule (first match wins, top to bottom) |
|---|---|
| `Done` | closed, and its PR (or every final PR of a parent) is merged |
| `Closed (no merge)` | closed otherwise |
| `Merged` | open, and its PR (or every final PR) is merged |
| `PR open` | open, and its PR (or a final PR) is open |
| `Broken down (n/m)` | `ai:parent`; n = closed sub-issues, m = all sub-issues |
| `Implementing` | `ai:implementing`, no PR yet |
| `Ready - complex` | `ai:ready` + `ai:complex` |
| `Ready - simple` | `ai:ready` + `ai:simple` |
| `Questions open` | `ai:questions-open` |
| `New` | none of the labels above |

- "Its PR": head `ai/issue-<n>` for a simple issue, `ai/issue-<p>-<o>` for a sub-issue, in the repo of its component
  (the rules `FinishIssueJob` and `SubIssueSequencer` use; reuse their helpers, do not copy them). Final PRs: head
  `feat/issue-<p>` in each repo that has sub-issues.
- Flags, shown next to the stage: `Failed` (`ai:failed`), `Claimed by X` (`ai:claimed`; the runner name comes from
  the job row when this runner holds it, else "another runner"), `On GitHub` (`ai:on-github` or `needs-cloud-agent`).
- Badge: `Queued` or `Running` when this runner's `jobs` table has a pending or running row for the issue.
- `Core/WaitingOn` gives the human action, or none: `Questions open` -> "answer the questions with @ai";
  `Ready - simple` without `ai-implement` -> "add ai-implement"; `Ready - complex` without `ai-breakdown` -> "add
  ai-breakdown"; `PR open` -> "review and merge PR #x"; `Failed` -> "read the failure, then remove ai:failed";
  `Broken down` with no sub-issue in `Implementing` or `PR open`, no queued job and `auto_start_first_sub_issue: false`
  -> "start the next sub-issue". A `Queued` or `Running` issue waits on nobody.
- Stage, flags and waiting-on are sent as display text plus a stable code (`stage: "pr-open"`,
  `stageLabel: "PR open"`). Angular never reads labels.

### Jobs and logs

- `jobs` gets a nullable `log_dir` column (the folder name under `logs/jobs/`), set when the job's `JobRun` folder is
  created. `cost_log` gets a nullable `copilot_calls` column. Both are added at startup with `ALTER TABLE` when missing.
- `copilot_calls`: analyze and review = 1, implement and fix = `result.Rounds.Count` (already counted for the detail
  text), breakdown = calls it makes, routine jobs = 0. It goes on `JobOutcome` and `JobWorker` writes it. Old rows
  stay empty and show as "-". It also goes to `cost.csv` as a new last column.
- Quiet jobs are still deleted (no change).
- Log viewer: lists the files of one job's `log_dir` and returns one file as text. The name must be a plain file name
  (no `/`, `\`, `..`, no rooted path), the resolved path must stay inside `logs/jobs/<log_dir>/`, files over 2 MB are
  cut with a note. No other file on disk can be read through the API.

### API (`/api`, JSON, read-only, UTC ISO-8601 times)

| Route | Returns |
|---|---|
| `GET /api/health` | runner status (Health) |
| `GET /api/overview` | running job, queue, waiting-on list |
| `GET /api/jobs?status=&type=&project=&page=` | job history page |
| `GET /api/jobs/{id}` | one job with its cost row |
| `GET /api/jobs/{id}/files`, `GET /api/jobs/{id}/files/{name}` | log viewer |
| `GET /api/issues?project=&component=&stage=&state=&flat=` | issue rows with stage, flags, badge, sub-issues nested unless `flat` |
| `GET /api/issues/{owner}/{repo}/{number}` | issue detail: stage, waiting-on, analysis summary from labels, sub-issues, PRs, job timeline |
| `GET /api/cost?by=day\|project\|issue&from=&to=` | Copilot calls, job count, total duration per group; an issue's total includes its sub-issues |

Only `GET`. Any other method returns 405.

### Angular app (`docs/adr/0002`)

- `src/AiRunner.Dashboard/`: latest stable Angular (check the version with ctx7 at the start), standalone components,
  signals, Angular Material. No NgModules.
- Screens: **Overview** (health strip, running job, queue, waiting on you), **Issues** (table, filters, nested
  sub-issues with a flat toggle), **Issue detail**, **Jobs** (history + log viewer; Markdown files rendered, others as
  text), **Cost**. The health strip is on every screen. Times shown in local time.
- Refresh: each screen polls its endpoint every 3 s while visible (stops when the tab is hidden). No websockets.
- API client and models generated from `/openapi/v1.json` (for example with `ng-openapi-gen`) into
  `src/app/api/`, committed. A C# field rename shows as a diff there and breaks the Angular build.
- Development: `npm start` runs `ng serve` with a proxy for `/api` to `http://127.0.0.1:<port>`.
- Release: `build-dashboard.ps1` at the repo root runs `npm ci`, regenerates the client, runs `ng build` and copies the
  output to the runner's `wwwroot/`. The build output is not committed (`.gitignore`). `dotnet build` and `dotnet test`
  never call npm.
- `CLAUDE.md` gets a short "Dashboard" section: the folder, the npm commands, the rule "no workflow logic in Angular",
  and regenerate the client after an API change.

### Delivery order (one GitBucket issue and one PR each; run the full test suite after each)

1. Host + shell: `dashboard:` config, `DashboardHost` (bind, host filter, port-taken warning), `RunnerStatus`,
   `/api/health`, OpenAPI, Angular project with Material shell + health strip, generated client, `build-dashboard.ps1`,
   not-built page, `CLAUDE.md` section.
2. Jobs: `log_dir` column, `/api/overview` (running + queue only), `/api/jobs*`, log viewer, Jobs screen, round in `RunnerStatus`.
3. Snapshot + stage: snapshot tables, poller writes (incl. PRs when review is off, slow refresh), `IssueStage`,
   `/api/issues` list, Issues screen.
4. Overview + issue detail: `WaitingOn`, waiting list in `/api/overview`, `/api/issues/{...}` detail, Overview and
   Issue detail screens.
5. Cost: `copilot_calls` column + `cost.csv`, `JobOutcome` count, `/api/cost`, Cost screen.

## Testing Decisions

- Good tests check external behaviour: what the API returns for a given state, what the snapshot holds after a poll,
  what `IssueStage` returns. Not private helpers or call order.
- **Pure Core:** `IssueStage` and `WaitingOn` as table tests, one case per row of the stage table plus the flags,
  the badge, sub-issue n/m and the "first match wins" order; inputs built with `Samples`.
- **Poller seam** (`FakeGitBucket`): snapshot after a poll (issues, open PRs with review off, slow refresh on poll 1
  and 10, retention and open-parent rules, removal of vanished issues); a snapshot error does not stop planning;
  planner decisions and fingerprints are unchanged.
- **API seam:** `MapDashboardApi` on a `TestServer` (`Microsoft.AspNetCore.TestHost` in the test project) over a
  temp SQLite file and a temp logs folder: every route; log viewer rejects `..`, `/`, `\`, rooted and unknown names;
  non-GET returns 405; a request with `Host: evil.example` is refused.
- **DashboardHost:** a taken port logs a warning and the host keeps running; not registered in `--once`/`--dry-run`.
- **Schema:** an old DB without `log_dir`/`copilot_calls` is upgraded at startup and old rows still read.
- **Angular:** unit tests for the health strip, the stage chip and the log path in the Jobs screen with Angular's default
  test runner; run by `build-dashboard.ps1`, not by `dotnet test`. No end-to-end tests in v1.
- Prior art: `JobTestKit`, `FakeGitBucket`, `Samples`, `JobPlannerTests`, `SqliteStateStore` tests, the parameter-count
  code rule. Tests need no network and never run Copilot.

## Out of Scope

- Any write action: start/retry/cancel a job, add labels, edit config (`docs/adr/0003`).
- More than one runner, a team view, login, LAN access.
- PRs without a linked issue on their own page (they show in Jobs only).
- Push updates (websockets, server-sent events).
- Showing issues the poller does not read (no trigger label and not assigned to the runner user).
- Counting premium requests by model multiplier (Copilot does not report it; calls are counted).
- Keeping quiet jobs.

## Further Notes

- The stage rules must reuse the helpers of `FinishIssueJob` / `SubIssueSequencer` for "its PR"; if that needs a
  helper moved into `Core`, move it, do not copy it.
- The snapshot is the only new GitBucket load: one extra PR list per code repo per poll when review is off, and the
  slow refresh every 10th poll.
- Do not run `--copilot-test`, `--once` without `--dry-run`, `--attachment-test` or the loop; the user tries the
  dashboard by running the loop themselves.
