---
status: proposed
supersedes: 0003
---

# Dashboard actions go through a code-host provider; every setting is edited live from the dashboard

GitBucket may be replaced (GitHub, Gitea, GitLab...), so the runner talks to the code host through one provider
interface, and GitBucket becomes its first implementation. The provider covers what the jobs use today: issues,
comments, labels, pull requests, closing an issue, attachments, and the git auth headers (branches are pushed and
fetched with git itself, so they need no provider call). Each provider also states what its host can and cannot do
(for example GitBucket: ASCII text only, no `/pulls/{n}/files`, an issue is closed through the web form), so a host
quirk stays in one class, as it does now in `GitBucketClient`.

The workflow state stays on the code host, whichever host it is: labels plus bot comments with markers. So
`JobPlanner` stays the only authority on what runs, and deleting `ai-runner.db` stays safe. A dashboard action that
moves an issue forward (analyze, implement, breakdown, retry, full review, answer questions, start the next
sub-issue) is a provider write (a label or a comment) and then wakes the poller; it never puts a job straight into
the queue. Runtime controls that are not workflow state (poll now, pause or resume the runner, cancel the running
job) act inside the process.

Every setting can be changed on the dashboard and takes effect without a restart. `repos.yml` stays the one place
settings live (reviewable, and the database stays a cache): the runner writes it itself, after the same strict
resolver accepts the new content, and keeps the previous file as a backup. The comments in `repos.yml` are not
kept; their text becomes field help on the settings screen. A new config is swapped in between jobs: the poller
reads it on its next poll and a running job finishes with the config it started with. A setting that cannot change
in a running process (the dashboard port, `paths.state`) is saved and marked "restart needed". Secrets that live
in `repos.yml` (`git_bucket.web_password`) can be set but are never sent to the browser. The token stays in user
secrets or an environment variable and never goes through the dashboard; the settings screen only says whether it
is set.

Consequences:

- No login for now. The dashboard stays on `127.0.0.1`. Every write endpoint gets a CSRF guard from the first one
  (a custom header plus an `Origin` check), because any web page the user opens can POST to `127.0.0.1`.
- Editable `commands` and `allow_shell` mean that whoever reaches the dashboard can run commands on this laptop.
  Before it is opened to teammates on the LAN, write endpoints need at least a shared token.
- The provider and the live settings each get a spec before anything is built. `docs/specs/dashboard.md` keeps its
  read-only v1 scope.
