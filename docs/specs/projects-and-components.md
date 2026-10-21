# SPEC: Projects and components (multi-repo, issues repo, monorepo)

The workspace changes of `agent-cohesion.md` (instruction files, checkout of the other repos at the feature branch) build on this spec.

Status: ready for an implementing agent.
Read `CLAUDE.md` first. Its architecture, security and code-style rules apply to every line of this work.

## Problem Statement

Today one job checks out one repo: the repo that holds the issue. Real projects are often split. One project has a
backend repo and a frontend repo. Another keeps its issues in a separate repo. A third is a monorepo with several apps
in folders. In all of these cases the agent only sees part of the project. It cannot check that the frontend calls the
backend API the right way, it cannot plan work that spans both sides, and it opens sub-issues and PRs in the wrong
place. Other people and teams will use the runner too, with their own repo layouts and their own settings, and today
every setting is copied per repo in `repos.yml`.

## Solution

Add a **project** concept. A project groups several **components**. A component is a repo plus an optional folder
(`path`) inside it. Every job prepares a workspace with all components of the project, so the agent sees the full
picture. Code is only written, committed and turned into a PR in the one component the issue belongs to. Issues and
sub-issues always live in one place: the project's `issues_in` component.

The same rules work for all three layouts without special cases, because every decision goes through
`component.repo` and `component.path`:

- multi-repo: one component per repo, `path: .`;
- separate issues repo: an extra component with `code: false` that holds issues and docs;
- monorepo: several components share one repo, each with its own `path`. The repo is cloned once.

Settings get global `defaults`, and each project overrides only what it needs. `repos.yml` moves to snake_case keys.
The old flat `repos:` list still works: each entry becomes a project with one component. Optionally, several runners
can work on the same projects without doing the same job twice ("claims").

## User Stories

Config

1. As a runner owner, I want to describe a project as a set of named components, so that the runner knows which repos belong together.
2. As a runner owner, I want a component to be a repo plus a folder, so that a monorepo app can be a component.
3. As a runner owner, I want components that share a repo to share one clone, so that disk and fetch time are not wasted.
4. As a runner owner, I want to mark a component `code: false`, so that an issues/docs repo is never changed and never gets a PR.
5. As a runner owner, I want to name the component that receives issues (`issues_in`), so that all issues and sub-issues of a project live in one place.
6. As a runner owner, I want to point to a PROJECT.md inside the issues component, so that the agent reads a short project description first.
7. As a runner owner, I want global `defaults` for trigger label, trigger keyword, models, limits, workflows, review settings and never-edit paths, so that I do not copy the same values for every project.
8. As a team lead, I want my project to override only the keys I care about, so that the rest follows the shared defaults.
9. As a runner owner, I want build/test/lint commands per component, so that a .NET backend, an npm frontend or any other stack each use their own commands.
10. As a runner owner, I want project-level commands that components can override, so that a monorepo with one root build is set once.
11. As a runner owner, I want to choose whether commands run in the component folder or at the repo root, so that root-based tools (gradle, nx, a root solution) work without `cd` tricks.
12. As a runner owner, I want my old flat `repos:` list to keep working, so that a single-repo setup needs no new structure.
13. As a runner owner, I want startup to fail on an unknown or misspelled key, so that a typo never silently falls back to a default.
14. As a runner owner, I want startup to fail when a repo is in two projects, `issues_in` names a missing component, or a project has no code component, so that config mistakes are found at once.
15. As a runner owner, I want all keys in snake_case, so that the file has one consistent style.

Workspace

16. As the agent, I want every component of the project checked out under one folder, so that I can read all the code.
17. As the agent, I want a `components.md` that lists each component, its folder, its AGENTS.md and whether I may write there, so that I know where things are.
18. As a runner owner, I want an existing clone to be fetched and reset instead of cloned again, so that jobs stay fast.
19. As a runner owner, I want runner files (context.md, plan.md, PR_BODY.md, templates) outside every git clone, so that they are never committed.

Analysis

20. As the agent, I want to read PROJECT.md, then components.md, then the relevant AGENTS.md files, so that I understand the project before analysing.
21. As a product owner, I want the analysis to say which components are affected, so that I see the scope at a glance.
22. As a product owner, I want an issue that touches more than one component to always be `complex`, so that it is split into one sub-issue per component.
23. As an implementer, I want each step of the SPEC technical plan to name its component, so that I know which code each step changes.
24. As a runner owner, I want an analysis that breaks these rules to fail clearly, so that a bad output never reaches the next step.

Breakdown

25. As a product owner, I want each sub-task to name its component, so that each sub-issue is built in the right repo.
26. As a product owner, I want all sub-issues created in the issues repo with `Component:` and `Parent: #<n>` lines, so that the whole plan is visible next to the parent issue.
27. As a developer, I want a `feat/issue-<n>` branch in every repo that has at least one sub-issue, so that the sub-issue PRs collect in one place per repo.

Implement

28. As the runner, I want to read the sub-issue from the issues repo and resolve its component to a repo and path, so that I know where to write.
29. As a reviewer, I want commits only in the target component's repo and only under its path, so that a monorepo change never leaks into another app.
30. As a reviewer, I want edits outside the target component, in sibling repos, or to never-edit files undone and listed in the PR, so that I see what the agent tried and the PR stays in scope.
31. As a reviewer, I want the PR opened in the component's repo with a link `owner/issues-repo#<n>`, so that I can find the issue and SPEC from the PR.
32. As a product owner, I want the runner to comment the PR link on the issue and close it after the PR is merged, so that the issue tracker stays correct even across repos.
33. As a product owner, I want the next sub-issue to start after the previous PR is merged, so that the sequence continues across components.
34. As a product owner, I want one final PR per repo once all sub-issues are done, and the parent closed when all final PRs are merged, so that backend and frontend reach the default branch together.
35. As a runner owner, I want diff limits counted in the target repo only, so that sibling repos do not count against the limit.

Review and review fix

36. As a reviewer, I want the review to see sibling components read-only, so that it can check API contracts between frontend and backend.
37. As a reviewer, I want the review to find the SPEC by following the issue link in the PR body to the issues repo, so that the spec check works across repos.
38. As a developer, I want `@ai` on a PR to fix code only in the component that the PR belongs to, so that a fix never touches another component.

Poller and labels

39. As a runner owner, I want issues read only from each project's issues repo, so that polling is cheap and predictable.
40. As a runner owner, I want PRs polled from every repo that holds code, so that every component's PRs are reviewed.
41. As a runner owner, I want `--setup-labels` to create the labels in every repo of every project, so that one command prepares everything.
42. As a team, I want our own trigger label and keyword (for example `bot` / `@bot`), so that we can share a GitBucket with other teams.
43. As a team, I want a model per workflow per project, so that we pay for a strong model only where it matters.

Claims (multi-runner)

44. As a runner owner, I want to turn claims on per project, so that several runners can share projects safely.
45. As a runner owner, I want a job to claim its issue or PR with a label and a comment naming my runner, so that other runners skip it.
46. As a runner owner, I want a claim older than 30 minutes with no result to be taken over, so that a crashed runner does not block an item forever.
47. As a reader of an issue, I want one claim comment per item that is edited in place, so that claims do not flood the thread.

Docs

48. As a new team, I want README examples for the three layouts and a migration note, so that I can set up my project without reading the code.

## Implementation Decisions

### Config schema (snake_case only)

The whole file uses snake_case. The loader rejects any unknown key with the line number and, when it can, a hint
(`unknown key 'maxDiffLines' (did you mean 'max_diff_lines'?)`). There is no camelCase support.

Target shape (this is the contract; values are examples):

```yaml
code_host: { kind: gitbucket, base_url: http://host:3000 }   # old name: git_bucket: (no kind)
poll_interval_seconds: 90
runner_name: ""                    # global; empty = machine user name; used by claims
copilot: { command: copilot, analyze_timeout_minutes: 15, review_timeout_minutes: 15, implement_timeout_minutes: 45, extra_args: [] }
paths: { work: work, logs: logs, state: ai-runner.db }
log_retention_days: 30
attachments: { ... same keys as today, snake_case ... }
git: { user_name: ai-runner, user_email: ai-runner@localhost }

defaults:                          # every key here may be overridden per project
  trigger_label: ai
  trigger_keyword: "@ai"
  workflows: [analyze, review, breakdown, implement, fix]
  models: { analyze: <model>, implement: <model>, review: <model>, fix: <model> }
  limits: { max_changed_files: 15, max_diff_lines: 800, max_analysis_rounds: 5, max_review_fixes: 5 }
  implement: { max_fix_rounds: 2 }
  review: { scope: scoped, skip_paths: [] }
  never_edit: [migrations/, "*.secrets.json", Jenkinsfile, .github/]
  auto_start_first_sub_issue: false
  claim_enabled: false

projects:
  mbh:
    issues_in: issues
    context: PROJECT.md            # optional, default PROJECT.md, resolved inside the issues_in component folder
    limits: { max_diff_lines: 400 } # project override example
    commands: { ... }              # optional project-level fallback for all components
    components:
      issues:   { repo: org/mbh-issues, path: ., code: false }
      backend:  { repo: org/mbh-backend, path: ., commands: { setup: "", build: "...", test: "...", lint: "...", allow_shell: [], run_in: component } }
      frontend: { repo: org/mbh-frontend, path: . }

repos:                             # old flat format, still supported
  - owner: org
    name: single-repo
    workflows: [...]
    commands: { ... }
    limits: { ... }                # any settings key; becomes the project's overrides
```

Rules:

- Three levels. **Global only:** `code_host` (old name `git_bucket`), `poll_interval_seconds`, `runner_name`, `copilot`, `paths`,
  `log_retention_days`, `attachments`, `git`. **Defaults, overridable per project:** the `defaults` keys above.
  **Per component:** `repo`, `path`, `code` (default true), `commands`.
- `commands` is never in `defaults` (a global command would assume a stack). Component `commands` override
  project `commands` key by key.
- `commands.run_in`: `component` (default) runs commands in `<clone>/<path>`; `repo` runs them at the clone root.
- Merge rule: objects merge key by key; lists replace the default list (never appended).
- Model keys are workflow names: `analyze`, `implement`, `review`, `fix`. Empty or missing = Copilot default.
- `trigger_label` replaces the old global `scopeLabel`.
- Old flat `repos:` entry -> project named after the repo name, one component with the same name, `repo: owner/name`,
  `path: .`, `code: true`, `issues_in` = that component. The entry's settings keys become project overrides.
  `repos:` and `projects:` may both be present.
- Validation (startup fails with a clear message):
  - a repo belongs to exactly one project (several components of that project may share it);
  - project and component names: lowercase letters, digits, `-`, `_` (they become folder names);
  - `issues_in` names an existing component;
  - each project has at least one `code: true` component;
  - no two distinct repos in one project share a repo name (the clone folder is the repo name);
  - `path` is relative, uses `/`, has no `..`; `.` means the whole repo;
  - two `code: true` components in the same repo may not have overlapping paths (a `code: false` component may overlap);
  - a project name may not collide with a legacy entry's project name;
  - existing checks stay (`base_url` set, known workflow names).

### Resolved model

- The loader produces resolved objects: `ProjectConfig` (name, merged `Settings`, components, issues component,
  context path) and `ComponentConfig` (name, repo full name, path, code flag, merged commands, run_in).
- `RepoConfig` disappears. A lookup `FindProject(repo)` returns the project for any repo full name. Every consumer
  that read `RepoConfig` (planner, loader, poller, jobs, label setup) reads `project.Settings` and components instead.
  `Core` may depend on these Configuration POCOs, as today.
- `JobRequest` keeps its shape (`Type`, `Repo`, `Number`, `Payload`). For issue jobs `Repo` is the issues repo; for PR
  jobs it is the component repo. The SQLite schema does not change.

### Trigger keyword and label

- `trigger_keyword` drives the mention detection (built from the escaped keyword with the same word-boundary rule as
  today's `@ai` regex) and every user-facing message that today says "@ai".
- `trigger_label` is used for the issue filter, the label put on new sub-issues, PR scope and label setup.
- All other label names (`ai:ready`, `ai-implement`, ...), branch names and markers stay fixed.

### Poller

- Issues: for each project, list open issues in the `issues_in` repo only (by trigger label, plus assigned to the
  runner user, as today). Issues in other repos are ignored.
- PRs: list open PRs in every distinct repo that has at least one `code: true` component.
- Fingerprints stay keyed by repo + number.
- `--setup-labels` creates all workflow labels (with each project's `trigger_label`) in every distinct repo of every
  project, including `code: false` repos.

### Workspace

- `work/<project>/` is the project root. Each distinct repo is cloned once to `work/<project>/<repo-name>/`; an
  existing clone is fetched and reset (same hardened git as today: env-based auth, hooks off).
- A project prepare request names the target repo with its start point and optional new branch (and extra fetch
  refspecs). Every other repo is checked out detached at `origin/<default branch>`. All components, including
  `code: false`, are checked out for every job.
- Before each job the runner deletes stale runner files at the project root (`context.md`, `plan.md`, `PR_BODY.md`,
  `fix.md`, `diff.patch`, `changed-files.txt`, `components.md`, `.ai-workflow/`), because `git clean` does not reach
  them. Templates are copied to `work/<project>/.ai-workflow/templates/`. Attachments go to
  `work/<project>/.ai-workflow/attachments/`.
- `.git/info/exclude` with the runner file names is still written in every clone (safety net if the agent writes a
  runner file inside a clone).
- A new pure Core builder writes `components.md`: one row per component with name, repo, path, absolute folder,
  `code` yes/no, `issues_in` yes/no, AGENTS.md path (component folder's AGENTS.md, else the repo root's, else "none"),
  and the role in this job (`target - you may write here` or `read-only`). It also shows the PROJECT.md path, or says it
  is missing (missing is not an error).
- Copilot always starts in `work/<project>/` for all four agents (analyze, review, implement, fix). No `--add-dir`.
  Consequence (accepted): a product repo's own `.github/agents/` files no longer replace the runner's agents.
- The `.git/config` guard covers every clone of the project. If any clone's `.git/config` changed, that clone is
  discarded, nothing is pushed and the job fails.

### Analysis

- `templates/analysis.md` gets `**Components affected:** <names, comma separated> | all` right after Complexity.
  When Status is `questions-open` the value may be `unknown`.
- `templates/spec.md` technical plan becomes `| Step | Component | File | Change |`. File is relative to the component folder.
- `agents/grill-issue.agent.md`: read PROJECT.md (if present), then components.md, then the AGENTS.md of the
  components the issue touches; name only `code: true` components; more than one component means `complex`;
  breakdown rows each name one component.
- Prompt for analysis mentions components.md and PROJECT.md.
- `AnalysisParser` reads Components affected. Names must be `code: true` components of the project; `all` means all
  code components. When Status is `ready` the field is required and may not be `unknown`. If it resolves to more than
  one component and Complexity is `simple`, the output is invalid (`FormatException`, job fails like a bad Status).
- A new SPEC parser checks the technical plan table: 4 columns, component names valid (single names, not `all`).
  A bad SPEC fails the job the same way.
- Status / Complexity / Verdict vocabularies and all markers stay unchanged.

### Breakdown

- Breakdown table becomes `| # | Component | Title | Scope | Touches | Depends on |` (6 columns, Component required,
  one `code: true` component name). `Shared contract:` stays.
- Sub-issues are always created in the `issues_in` repo (never in component repos). The sub-issue body gets a
  `Component: <name>` line next to `Parent: #<n>`, `Order:`, `Base branch:` and `Depends on:`. `Parent: #<n>` is a
  plain same-repo link because sub-issue and parent live in the same repo.
- `feat/issue-<n>` is created (from `origin/<default>`, kept if it exists) in every distinct repo that has at least
  one sub-issue. In a monorepo both components share that one branch.
- The parent comment keeps the `<!-- SUBISSUES: ... -->` marker and lists each sub-issue with its component.
- Sub-issue order stays one global sequence across components, following `Depends on`.

### Implement

- Target component: a sub-issue's `Component:` line; a simple issue's `Components affected` (exactly one component).
- Compatibility fallback for data written before this change (no Component line / no Components affected): if the
  project has exactly one code component, use it; otherwise fail with "write @ai to re-run the analysis". This is a
  data fallback by count, not a layout case.
- Branch names stay `ai/issue-<n>` and `ai/issue-<parent>-<order>`; base stays the default branch or
  `feat/issue-<parent>`. They are created only in the target component's repo.
- plan.md names the target component, its folder, and its commands. `agents/implement-issue.agent.md`: write only
  inside the target component folder; other components are read-only; components.md shows the roles.
- The setup command and the checks (build/test/lint) are the target component's, run per `run_in`. Shell
  permissions come from the target component's commands (same `ShellPermissions` rules as today).
- **Scope guard** (in `AgentSession`, so implement and fix both get it). After every agent round and before the checks:
  - every non-target clone: any change (tracked or untracked, ignored files excluded) is undone;
  - target clone: changes outside the allowed component path(s), and changes matching `never_edit`, are undone
    (tracked files restored from HEAD, untracked files deleted);
  - every undone path is recorded. The list goes into the PR body ("Changes undone by ai-runner") and, if a fix round
    happens anyway, into fix.md. An undo alone does not trigger a fix round.
  - The path rules live in a new pure Core class (component path check + `never_edit` matcher). The git side needs new
    `IGitClient` methods: list changed + untracked paths, restore paths.
- `never_edit` matching, against the path from the repo root, case-insensitive, gitignore-like: trailing `/` = a
  folder with that name anywhere; no `/` = a file name anywhere; a `/` in the middle = anchored at the repo root;
  `*` and `**` wildcards.
- Size limits (`max_changed_files`, `max_diff_lines`) count the target repo only, after the guard.
- The PR opens in the component's repo. The runner appends `Issue: <issues owner/repo>#<n>` as the last line of the
  body. It never writes `Closes` (in any layout).

### Closing after merge

- Sub-issues: `SubIssueSequencer` and its job read PRs in each sub-issue's component repo. A slot is done when the
  sub-issue is closed, or the PR head `ai/issue-<p>-<o>` into `feat/issue-<p>` in that repo is merged. On a merged PR
  whose sub-issue is still open, the job comments the PR link on the sub-issue, closes it, and then starts the next
  sub-issue as today.
- Final PRs: when all sub-issues are done, open one final PR per repo that has sub-issues (head `feat/issue-<p>` into
  the default branch), unless one already exists in that repo. Each final PR body links the parent with
  `Issue: <issues owner/repo>#<p>` and lists that repo's sub-issue PRs. One parent comment lists all final PRs.
  When all final PRs are merged, the runner comments and closes the parent.
- Simple issues: a new `JobType.FinishIssue`, planned as a routine decision for an open issue with `ai:implementing`,
  without `ai-implement`, `ai:parent` or `ai:sub-issue`. The job finds the PR (head `ai/issue-<n>`) in the target
  component's repo. Merged: comment the PR link, remove `ai:implementing`, close the issue. Closed without merge:
  remove `ai:implementing` and comment. Still open or no PR yet: do nothing (quiet).
- Everything is derived from GitBucket data on each poll, so a runner that was off during a merge catches up.

### Review and review fix

- Linked issue from the PR body, in this order: `Issue: owner/repo#n` line; any `owner/repo#n`; today's
  `close/fix/resolve #n` (same repo); first `#n` (same repo). The issue is read from its own repo; SPEC and analysis are
  found as today.
- Review prepares the whole project with the PR repo as target (detached at the PR head). Siblings are read-only.
  `agents/pr-review.agent.md` reads components.md and checks contracts (API paths, DTO names, shared contract) against
  sibling components when the change touches them. Review template and Verdict vocabulary unchanged.
- Review fix target component, in this order: the linked issue's `Component:` line; the linked issue's Components
  affected; with no link, every `code: true` component in the PR's repo (the guard then allows all their paths).
  `WhyNotFix` uses the target component's commands.

### Claims (optional, `claim_enabled`)

- New label `ai:claimed` (in the label list and `--setup-labels`). Global `runner_name`, default the machine user name.
- Before a job runs, `JobWorker` asks a claim service to claim the item (issue or PR from the request; whether it is a
  PR comes from `IJob.Info`, no switch on job type). Routine jobs (`SequenceSubIssues`, `FinishIssue`) do not claim up
  front; they claim through the job run just before their first write. A "wait" result claims nothing.
- A claim = the `ai:claimed` label + one claim comment per item (with its own hidden claim tag), edited in place on each
  new claim: "Claimed by <runner_name>". Its updated time is the claim time.
- Race: after writing, the runner re-reads; the oldest claim comment is the claim, and it proceeds only if that comment
  names it. A runner that created a newer duplicate edits it to "withdrawn" and backs off (comments are never deleted).
- Claimed by another runner -> the job ends as `NotNeeded(Quiet)`. A claim older than 30 minutes with no bot comment
  after it can be taken over.
- The label is removed when the job ends (success or failure); the comment is edited to "released".

### Docs

- README: project concept, three layout examples, settings levels, migration note (snake_case key renames, the
  `projects:` form, run `--setup-labels`, delete old `work/<owner>/` clones, move open issues to the issues repo).
- `CLAUDE.md`: architecture notes for projects, workspace root, scope guard, closing after merge, claims.
- `onboarding/`: a PROJECT.md template (simple English).
- This repo's `repos.yml` becomes: project `mbh`, `issues_in: issues`, components `issues`
  (`manhhai/mbhanalytics-issues`, `code: false`), `backend` (`manhhai/mbhanalytics`, today's dotnet commands),
  `frontend` (`manhhai/mbhanalytics-fe`, today's npm commands); today's other values move to `defaults`; `never_edit: []`.

### Delivery order (one PR each; run the full test suite after each and summarize)

1. Config + workspace + defaults: schema, loader, validation, merge, legacy mapping, `FindProject`, all consumers moved
   off `RepoConfig`, trigger label/keyword, poller sources, project workspace, components.md, agents at project root,
   `run_in`, repos.yml migrated.
2. Analysis + breakdown: templates, agent file, AnalysisParser, SPEC parser, BreakdownParser, SubIssueBody, breakdown
   job, Samples.
3. Implement + review + fix: target component, scope guard, PR link, size limits, sequencer across repos, final PRs,
   FinishIssue, linked issue across repos, review siblings, fix target.
4. Claims + docs + migration.

## Testing Decisions

- Good tests check external behaviour: what is written to GitBucket (issues, comments, labels, branches, PRs), what is
  on disk in the workspace, and what a parser returns or rejects. They do not check private helpers or call order.
- **Main seam: jobs through `JobTestKit`** (`FakeCodeHost`, fake Copilot, fakes for process/git). A shared test fixture
  provides three layouts: multi-repo, separate issues repo, monorepo. Every layout-dependent behaviour runs as a
  `[Theory]` over all three, with the same assertions:
  - analysis passes and fails on the components rules;
  - breakdown creates sub-issues only in the issues repo, with `Component:` and `Parent:` lines, and `feat/issue-<n>`
    in each repo with sub-issues (once in the monorepo);
  - implement pushes and opens the PR only in the component repo, with the `Issue:` link and no `Closes`;
  - sequencer comments + closes a sub-issue after merge and starts the next; final PRs per repo; parent closed;
  - FinishIssue closes a simple issue after merge;
  - review reads the SPEC across repos; fix picks the right target component;
  - poller reads issues only from issues repos and PRs from code repos;
  - claims: skip when claimed by another, take over after 30 minutes, release at the end.
  If a layout needs a special case to pass, stop and report it.
- **Config seam: `RunnerConfig.Parse(yaml)`**: snake_case, unknown keys rejected with hint, legacy `repos:` mapping,
  defaults -> project merge (objects merged, lists replaced), project/component commands merge, every validation error.
- **Real git seam** (like the existing `GitWorkspaceTests`, real `git` against `file://` bare repos): one clone per
  distinct repo, shared clone in a monorepo, fetch instead of re-clone, stale runner files removed, components.md
  content; scope guard really undoes changes outside the component path, `never_edit` matches and sibling-repo changes.
- **Pure Core tests via the filled templates in `Samples.cs`**: Components affected, SPEC Component column, 6-column
  breakdown table, sub-issue body Component/Parent lines, linked-issue parsing, `never_edit` matcher cases.
- Prior art: `JobTestKit` and `FakeCodeHost` in the test fakes, `Samples`, `GitWorkspaceTests`,
  `RunnerConfigTests` and `JobPlannerTests`, and the code-rule test for the 3-parameter limit. All existing tests must
  stay green (update them where the config shape changes).
- Tests need no network and must not run Copilot or touch the real GitBucket.

## Out of Scope

- Workflow 4 (GitHub mirror / cloud agent) - still only skipped by the planner.
- Running several jobs in parallel; one job at a time stays.
- Issues or sub-issues in component repos; issues filed outside `issues_in` are ignored (README says where issues go).
- Per-component settings other than `commands` (limits, models, never_edit are per project).
- camelCase keys in repos.yml.
- Repo-local `.github/agents/` overriding the runner's agents.
- Moving existing issues between repos, or creating PROJECT.md automatically.
- Cross-project work (one issue spanning two projects).
- Relying on GitBucket close keywords across repos.

## Further Notes

- Constraints: simple English in comments, templates and docs; templates stay ASCII; no change to markers or to the
  Status / Complexity / Verdict vocabularies; the agent never runs git and never holds tokens; all git and API calls
  stay in the runner. Follow the `CLAUDE.md` code rules (braces, blank lines, names, max 3 parameters -> DTO records).
- Changing a template means changing its parser, its agent file and `Samples.cs` together.
- Working method for the implementing agent: for exploration (reading and searching the codebase) use subagents on a
  cheaper model (Sonnet or Haiku); keep the main model for reasoning and code changes.
- Risks:
  - Copilot started in a folder that is not a git repo: a trust prompt in `-p` mode is not confirmed by the docs, and
    the local CLI is old (0.0.421). The user should run `copilot update` and one cheap `copilot -p` in an empty non-git
    folder before using PR 1 for real. The same run can check whether `--deny-tool='write(.ai-workflow)'` covers files
    inside the folder.
  - Copilot reads instruction files from the start folder upward only, so component AGENTS.md files are read because
    the agent files tell the agent to open them (via components.md), not automatically.
  - Undoing out-of-scope edits can break the build; that costs a fix round or gives a red PR.
  - Every job now fetches every repo of the project (slower jobs).
  - PR 1 is the largest change: `RepoConfig` is replaced everywhere, including many planner tests.
- Manual steps for the user after PR 1: run `--setup-labels`; add PROJECT.md to `manhhai/mbhanalytics-issues`; move
  or finish open `ai` issues in `manhhai/mbhanalytics` before switching (they will no longer be polled).
- Do not run `--copilot-test`, `--once` without `--dry-run`, `--attachment-test` or the loop; they spend premium
  requests or touch the real GitBucket.
