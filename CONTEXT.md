# ai-runner

A runner on a developer laptop that watches issues and pull requests on GitBucket and answers them with analyses, sub-issues, code and reviews written by an AI agent.

## Language

**Bot comment**:
A comment the runner posted itself, found again by its hidden marker.
_Avoid_: AI comment, runner comment

**Block**:
A marked section inside a bot comment, one per kind (analysis, spec, review, review fix).
_Avoid_: section, message

**Thread**:
The bot comments of one block kind on one issue or pull request, together with the human comments around them.
_Avoid_: conversation, discussion

**Trigger**:
The newest human comment that mentions the project's trigger keyword (`@ai` by default) and that no reply has answered yet.
_Avoid_: request, ping

**Reply**:
A bot comment that answers a trigger: an analysis (one **Round**) or a **Review fix**.
_Avoid_: response, answer

**Read-up-to**:
The newest comment id a job had read when it posted its reply; the reply records it, so comments written while the agent ran still count as new.
_Avoid_: context-upto (the stored tag), last read

**Answered-up-to**:
The point after which comments are new: the later of the last reply's read-up-to and the local cursor.
_Avoid_: handled-up-to, last handled

**Local cursor**:
The newest `@ai` comment the runner's own database says was handled; it covers a job that failed before it could reply.
_Avoid_: last-handled comment

**Code host**:
The server that holds the issues, pull requests and repos the runner works on. GitBucket today; the runner reaches it only through one provider, so it can be replaced.
_Avoid_: GitBucket (for the role), forge, server

**Setup mode**:
How the runner starts in its loop when it has no code-host token yet: only the dashboard runs, nothing is polled and no job runs, until the token is saved on the dashboard's Setup page. Every other mode stops instead.
_Avoid_: first run, onboarding mode, unconfigured

**Project**:
A group of components that are worked on together; its issues live in one place (`issues_in`). Every job checks out all components of one project.
_Avoid_: workspace, product

**Component**:
A repo plus an optional folder (`path`) in it. A job changes code in exactly one component; the others are read-only. Components that share a repo share one clone.
_Avoid_: module, service, package

**Instructions**:
The repository files that hold the rules for the agent (`instructions` of a component in `repos.yml`, default `AGENTS.md`). The runner copies them in front of the agent on every job, in the `AGENTS.md` of the project folder.
_Avoid_: rules file, conventions

**Contract**:
The SPEC's API, UI and shared-name tables. It is binding for every sub-issue: the runner copies the parent's SPEC into each sub-issue's plan, so the sub-issues agree on one contract.
_Avoid_: interface, agreement

**Round**:
One analysis reply on an issue, numbered and capped by a limit.
_Avoid_: iteration

**Attachment**:
A file a person linked from issue, comment or PR text through GitBucket's `_attached` route. The runner downloads it into the clone for the agent; it never uploads anything.
_Avoid_: upload, resource

**Review fix**:
One reply on a pull request after the agent changed the PR's branch to answer an `@ai` comment, numbered and capped by a limit.
_Avoid_: AI fix, patch round

**Stage**:
Where an issue is in the workflow, from new to done, worked out from its labels, its pull requests and its sub-issues. It is separate from whether the issue is open or closed on GitBucket: an issue can be merged and still open.
_Avoid_: status, state, phase

**Lane**:
One of five groups of stages (to do, ready, in progress, in review, done), shown side by side on the dashboard's
issue board. The "to do" lane holds new issues and issues with open questions; it is not what an issue waits on.
_Avoid_: column, bucket, phase

**Waiting on**:
The one action a person must take before an issue can move to its next stage, such as answering questions or merging a pull request. An issue that is queued or running waits on nobody.
_Avoid_: blocked, pending, todo

**Action**:
Something a person asks the runner to do from the dashboard (implement, break down, retry, answer, ask, start the next sub-issue, full review, fix). It is only a write on the code host, a label or a person's `@ai` comment, that the planner then picks up like the same change made by hand; it never puts a job in the queue. The runner offers an action only while the item still allows it, and checks again on fresh data right before it writes.
_Avoid_: command, button, trigger

**Workspace lock**:
The one lock per project folder under `work/`. A job takes it when it prepares the project and the worker releases it when the job ends; a workspace action from the dashboard (fetch, re-clone) tries it without waiting and is refused with a 409 while a job holds it. Deleting an orphan needs no lock because no project owns that folder.
_Avoid_: mutex, semaphore

**Orphan**:
A folder under `work/` that is no project of `repos.yml` (a project that was removed or renamed). The dashboard may delete it, and only it: a plain folder name that resolves directly inside `work/`, never a project folder.
_Avoid_: stale folder, leftover

**Snapshot**:
The runner's own copy of the issues and pull requests it saw on its last polls, kept only so the dashboard can show them. It can be thrown away at any time and is rebuilt by the next polls.
_Avoid_: cache, mirror, sync

**Health**:
Whether the runner is working: when it last polled, the last error it hit, and whether it is busy or idle.
_Avoid_: status, heartbeat
