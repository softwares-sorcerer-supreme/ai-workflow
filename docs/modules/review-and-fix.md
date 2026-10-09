# Module: PR review and review fix (Workflows 3 and 3b)

## Purpose

- **Review (3)**: give each new head commit of an in-scope PR one first-pass AI review, as a single comment that is updated in place. It is advice for the human reviewer, never a gate.
- **Review fix (3b)**: when someone writes `@ai` on an in-scope PR, let Copilot change the PR's branch to answer the review and comments, then reply on the PR. The fixed head is then reviewed again automatically.

## Components

```mermaid
flowchart LR
    RJ["ReviewPullRequestJob"]
    FJ["FixReviewJob"]
    AS["AgentSession<br/>(shared with implement)"]
    JS["JobSupport<br/>PlanPullAsync, LinkedIssueAsync"]
    JP["JobPlanner<br/>PlanPull, WhyNotFix"]
    AT["AtAiThread"]
    CB["ContextBuilder<br/>ForPull, ForReviewFix"]
    RP["ReviewParser"]
    RR["ReviewReplyParser"]
    PG["PathGlob"]
    GC["IGitClient"]
    GB["IGitBucketClient"]
    ST[("IStateStore<br/>review_state, issue_state")]

    RJ --> JS
    FJ --> JS
    JS --> JP
    JP --> AT
    RJ --> CB
    RJ --> RP
    RJ --> PG
    RJ --> GC
    RJ --> GB
    RJ --> ST
    FJ --> AS
    FJ --> AT
    FJ --> CB
    AS --> RR
    FJ --> GB
    FJ --> ST
```

*Both jobs start by planning the PR through `JobSupport`. The review is read-only. The fix reuses the implement loop with a different `AgentTask`.*

## Key files

| File | One line |
| --- | --- |
| `Jobs/ReviewPullRequestJob.cs` | Workflow 3. Diff, context, `pr-review` agent, parse, post or update the review comment. |
| `Jobs/FixReviewJob.cs` | Workflow 3b. Refusal rules, context, `fix-review` agent via `AgentSession`, push, reply. |
| `Core/ReviewParser.cs` | Checks Verdict (`looks good`, `needs changes`, `needs discussion`) and Severity (`bug`, `risk`, `style`) in the findings table. |
| `Core/ReviewReplyParser.cs` | Checks `REPLY.md`: headings `Replies`, `How it was tested`, `Open points`, and at least one `- ` reply line. |
| `Core/PathGlob.cs` | Small glob matcher for `review.skipPaths`. |
| `Core/ContextBuilder.cs` | `ForPull` (context.md), `ForReviewFix` (review-fix.md), `PlanForReviewFix` (plan.md), `LinkedIssue` (finds `Closes #n`). |
| `agents/pr-review.agent.md`, `agents/fix-review.agent.md`, `skills/review-checklist/` | Agent rules. |
| `templates/review.md`, `templates/review-reply.md` | Output formats. |

## Flow 1: review a PR

```mermaid
sequenceDiagram
    autonumber
    participant W as JobWorker
    participant R as ReviewPullRequestJob
    participant GB as GitBucket
    participant WS as Workspace and git
    participant Cop as Copilot (pr-review)
    participant S as SQLite

    W->>R: RunAsync(ReviewPullRequest, payload = head sha)
    R->>GB: LoadPullAsync (PR + plan again)
    alt decision is not Review (a fix is due, or already reviewed)
        R-->>W: NotNeeded
    end
    R->>WS: Prepare detached at head sha (also fetch refs/pull/N/head)
    R->>WS: git diff --name-only base...head
    alt no files, or all files match skipPaths
        R->>S: review_state = (sha, old comment id, "skipped")
        R-->>W: skipped (Quiet)
    end
    R->>WS: git diff base...head, write diff.patch, changed-files.txt
    R->>GB: LinkedIssueAsync (analysis + SPEC, or sub-issue body)
    R->>Cop: copilot --agent pr-review --deny-tool write,shell
    Cop-->>R: REVIEW block
    R->>R: RequireBlock, ReviewParser.Parse
    R->>GB: GetPull again
    alt head moved while Copilot ran
        R-->>W: outdated (nothing posted)
    end
    R->>GB: PATCH existing review comment, or POST a new one
    R->>S: review_state = (sha, comment id, verdict)
    R-->>W: ok "verdict: n bug, n risk, n style"
```

*Source: `Jobs/ReviewPullRequestJob.cs:33-92`. If the `PATCH` fails, a new comment is posted; the new id is stored.*

## Flow 2: review fix

```mermaid
sequenceDiagram
    autonumber
    participant H as Human
    participant GB as GitBucket
    participant F as FixReviewJob
    participant WS as Workspace and git
    participant S as AgentSession
    participant Cop as Copilot (fix-review)

    H->>GB: PR comment "@ai 1: fix, 2: skip"
    Note over F: Poller planned FixReview, payload = comment id
    F->>GB: LoadPullAsync, re-plan, comments
    F->>WS: Prepare at PR head sha, checkout PR head ref
    F->>F: JobPlanner.WhyNotFix (limit, default branch, branch sha)
    alt refused
        F->>GB: reply "AI fix - not started" + why
        F-->>F: SetLastHandledComment
    end
    F->>GB: LinkedIssueAsync gives plan.md
    F->>F: write review-fix.md (latest review, last reply, comments since reply)
    F->>S: RunAsync(task: agent fix-review, output REPLY.md, diff base = PR head)
    S->>Cop: round 1, checks, optional fix rounds
    alt round 1 failed
        F-->>F: throw "Nothing was pushed."
    end
    F->>S: SaveWorkAsync (commit + non-forced push to PR branch)
    alt push rejected
        F-->>F: throw "someone pushed meanwhile. Nothing was pushed."
    end
    F->>GB: reply: "AI fix N - commit abc1234, k files" + REPLY.md + checks report
    F->>F: SetLastHandledComment(ReadUpTo)
```

*Source: `Jobs/FixReviewJob.cs:44-93`. The reply carries the `context-upto` tag. A no-change fix still gets a reply ("no code change").*

## Flowchart: when is a review fix refused?

```mermaid
flowchart TD
    Q(["WhyNotFix(repo, pull, comments, defaultBranch, branchSha)"])
    L{"REVIEW_FIX replies >= maxReviewFixes?"}
    D{"PR head ref = default branch?"}
    B{"origin/head-ref sha = PR head sha?"}
    M1["Refuse: N fixes reached, finish by hand"]
    M2["Refuse: I never push to the default branch"]
    M3["Refuse: branch missing (fork or deleted) or moved"]
    OK["null: go ahead"]

    Q --> L
    L -->|"yes"| M1
    L -->|"no"| D
    D -->|"yes"| M2
    D -->|"no"| B
    B -->|"no"| M3
    B -->|"yes"| OK
```

*Source: `Core/JobPlanner.cs:135-148`. A refusal is still a **reply** on the PR, so it counts as handled and does not loop.*

## Business rules and edge cases

- **Review scope**: `scoped` (default) = label `ai` or runner-opened PR from `ai/*`/`feat/issue-*`. `all` = every open PR (including ones open at first start). Fix always uses the scoped rule.
- **Never reviewed**: closed/merged, draft, title starts `[skip-ai]`, no changed files, only `skipPaths` files changed.
- **"Created by Workflow 2: yes"** (stricter review, check the SPEC) is derived from the branch name `ai/issue-*` (`ContextBuilder.ForPull`, using `Branches.IsAiIssueBranch`). Only those PRs get a "Spec check".
- **Review comment is one per PR**: its id is saved in `review_state.comment_id`; if lost, the job finds the newest `REVIEW` bot comment on the PR.
- **Outdated head**: the review is dropped (nothing posted, nothing stored); the next poll reviews the new head.
- **Fix limits**: `limits.maxReviewFixes` counts replies (`AtAiThread.Done`). Each fix may spend `1 + maxFixRounds` premium requests.
- **Fix never pushes half-done work**: a round-1 failure throws; a push reject throws; both say "Nothing was pushed."
- **Failure bookkeeping**: PR jobs get no `ai:failed`. `ReviewPullRequestJob.OnFailed` stores the failed sha; `FixReviewJob.OnFailed` stores the trigger comment id. The failure comment tells the human to push a new commit (review) or write `@ai` again (fix).
- **Comment sources**: only the Conversation tab (issue-comments API). Inline diff comments are not read.
- **`diff.patch` for the fix** is a fix-round artifact (changes since the PR head), not the PR diff.

## Dependencies

- Uses: `IGitBucketClient`, `IWorkspace`, `IGitClient`, `ICopilotRunner`, `AgentSession`, `IStateStore`, Core parsers.
- Used by: `JobWorker`; planned by `Poller`.

## Gotchas

- **Order in the planner**: a fix is planned *before* a review. A review job that finds a fix is due returns `NotNeeded` (comment in `ReviewPullRequestJob.RunAsync`).
- **A fork PR** reaches `WhyNotFix` and gets a polite refusal because the workspace also fetches `refs/pull/N/head` (the fetch result is ignored if the server lacks it, `Workspace.cs:44-48`).
- **Two runners on one repo** with `scope: all` would both review every PR (README warning). There is no cross-runner lock; the only dedupe is the stored sha and the shared review comment.
- **Race on push**: if a person pushes while Copilot works, the non-forced push fails and the whole fix is thrown away (by design; the human writes `@ai` again).
- **Prompt-injection surface**: the review comments and PR text are fed to an agent that can write files. `ContextBuilder.ForReviewFix` says they are *data*, and `Markers.Wrap` neutralises `<!--` in output, but anyone who can comment on an in-scope PR can steer the code change (README "Security limits").
- **Tests**: `Jobs/FixReviewJobTests.cs` (15), `Jobs/ReviewAndSequenceJobTests.cs` (6), `Core/JobPlannerTests.cs` (23), `Core/AtAiThreadTests.cs` (13).
