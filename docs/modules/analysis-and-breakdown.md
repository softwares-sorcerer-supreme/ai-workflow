# Module: Analysis and breakdown (Workflows 1 and 1b)

## Purpose

Turn a vague issue into something safe to build. Copilot (read-only) asks questions and writes a plan; when the human has answered everything, it also writes a SPEC. For a complex issue, the runner splits the plan into ordered sub-issues and opens them one by one.

## Components

```mermaid
flowchart LR
    AJ["AnalyzeIssueJob"]
    BJ["BreakdownIssueJob"]
    SJ["SequenceSubIssuesJob"]
    CB["ContextBuilder.ForIssue"]
    AP["AnalysisParser"]
    BP["BreakdownParser"]
    MS["MarkdownSections"]
    SB["SubIssueBody"]
    SQ["SubIssueSequencer"]
    Cop["ICopilotRunner<br/>agent grill-issue"]
    GB["IGitBucketClient"]
    WS["IWorkspace + IGitClient"]

    AJ --> CB
    AJ --> Cop
    AJ --> AP
    AP --> MS
    AJ --> GB
    BJ --> BP
    BP --> MS
    BJ --> SB
    BJ --> WS
    BJ --> GB
    SJ --> SB
    SJ --> SQ
    SJ --> GB
```

*Analysis is the only one of the three that runs Copilot. Breakdown and sequencing are API-only; they read the breakdown the analysis already wrote.*

## Key files

| File | One line |
| --- | --- |
| `Jobs/AnalyzeIssueJob.cs` | Workflow 1. Builds `context.md`, runs `grill-issue`, validates, posts analysis (+ SPEC), sets labels. |
| `Jobs/BreakdownIssueJob.cs` | Workflow 1b step 1. Creates `feat/issue-N` and sub-issues, posts the parent comment with the hidden list. |
| `Jobs/SequenceSubIssuesJob.cs` | Workflow 1b step 2. Every poll for `ai:parent` issues: start next sub-issue or open the final PR. |
| `Core/AnalysisParser.cs` | Reads `**Status:**`, `**Complexity:**` and `**Components affected:**` -> labels to add/remove, components. |
| `Core/SpecParser.cs` | Checks the SPEC technical plan table (`Step | Component | File | Change`). |
| `Core/BreakdownParser.cs` | Reads the 6-column table (with `Component`) and `Shared contract:`. |
| `Core/SubIssueBody.cs` | Builds/parses sub-issue bodies, the hidden `SUBISSUES` list and `IsDone`. |
| `Core/SubIssueSequencer.cs` | Pure decision: `Wait`, `StartNext`, `OpenFinalPr`. |
| `Core/ContextBuilder.cs:ForIssue` | `context.md` for the agent (issue, then comments oldest first, bot comments tagged `[bot]`). |
| `agents/grill-issue.agent.md`, `skills/grill-me/SKILL.md` | The agent rules. |
| `templates/analysis.md`, `templates/spec.md` | Output formats. |

## Flow 1: an analysis round

```mermaid
sequenceDiagram
    autonumber
    participant W as JobWorker
    participant A as AnalyzeIssueJob
    participant GB as GitBucket
    participant WS as Workspace
    participant Cop as Copilot (grill-issue)
    participant S as SQLite

    W->>A: RunAsync(AnalyzeIssue, repo, n)
    A->>GB: LoadIssueAsync (issue, comments, re-plan)
    alt plan is not AnalyzeIssue
        A-->>W: NotNeeded (Quiet)
    end
    A->>A: thread.Next(maxAnalysisRounds)
    alt round limit reached
        A-->>W: throw JobFailedException (clarify in a call)
    end
    A->>WS: PrepareAsync(origin/default branch)
    A->>A: write context.md
    A->>Cop: copilot -p ... --agent grill-issue --deny-tool write,shell
    Cop-->>A: stdout with ANALYSIS block (+ SPEC block if ready)
    A->>A: RequireBlock, AnalysisParser.Parse
    A->>GB: AddComment(thread.Reply(analysis))
    A->>GB: ApplyLabels(add, remove)
    opt Status is ready
        A->>GB: AddComment(SPEC block)
    end
    A->>S: SetLastHandledComment(thread.ReadUpTo)
    A-->>W: Ok("round N, status, complexity")
```

*Source: `Jobs/AnalyzeIssueJob.cs:26-67`. Everything is validated **before** the first post, so a bad answer is never half-applied (comment at line 53). The SPEC is a separate comment so the implement job can find it by marker.*

### Label outcome (from `AnalysisParser.Parse`)

| Status | Complexity | Add | Remove |
| --- | --- | --- | --- |
| `questions-open` | any | `ai:questions-open` | `ai:ready`, `ai:simple`, `ai:complex` |
| `ready` | `simple` | `ai:ready`, `ai:simple` | `ai:questions-open`, `ai:complex` |
| `ready` | `complex` | `ai:ready`, `ai:complex` | `ai:questions-open`, `ai:simple` |

## Flow 2: breakdown

```mermaid
sequenceDiagram
    autonumber
    participant W as JobWorker
    participant B as BreakdownIssueJob
    participant GB as GitBucket
    participant G as Workspace and git

    W->>B: RunAsync(BreakdownIssue)
    B->>GB: LoadIssueAsync and re-plan
    B->>B: WhyNotBreakdown(labels)
    alt not ready+complex
        B->>GB: comment "I did not create sub-issues: ..." and remove ai-breakdown
        B-->>W: rejected
    end
    B->>B: latest ANALYSIS block, BreakdownParser.Parse
    B->>G: Prepare origin/default, if origin/feat/issue-N missing: push it
    B->>GB: ListOpenIssues(label ai:sub-issue)
    loop each task, in order
        B->>GB: reuse existing sub-issue (Parent+Order) or CreateIssue
        B->>GB: ApplyLabels (ai, ai:sub-issue, + ai:ready, ai:simple unless [developer])
    end
    B->>GB: AddComment(list with hidden SUBISSUES marker)
    B->>GB: ApplyLabels(add ai:parent, remove ai-breakdown)
    B-->>W: Ok(N sub-issues)
```

*Source: `Jobs/BreakdownIssueJob.cs:23-66`. Safe to retry: an existing feature branch is kept and existing sub-issues are matched by the `Parent:` / `Order:` header in their body, not by title.*

## Flow 3: sequencing decision

```mermaid
flowchart TD
    S(["Decide(parent, subs in order, pulls)"])
    Loop["next sub, order k"]
    Done{"IsDone(sub)?<br/>closed, or PR ai/issue-P-k<br/>merged into feat/issue-P"}
    First{"k = 1?"}
    InProg{"has ai-implement,<br/>ai:implementing or ai:failed?"}
    Simple{"has ai:simple?"}
    W1["Wait: waiting for sub 1 to be started"]
    W2["Wait: in progress"]
    W3["Wait: waits for a developer"]
    Start["StartNext: add ai-implement to sub k,<br/>comment 'previous is done'"]
    All{"all done?"}
    FinalPr{"PR from feat/issue-P exists?"}
    W4["Wait: final PR already exists"]
    Open["OpenFinalPr: PR feat/issue-P into default,<br/>'Closes #parent'"]

    S --> Loop
    Loop --> Done
    Done -->|"yes"| All
    All -->|"more subs"| Loop
    All -->|"no more"| FinalPr
    FinalPr -->|"yes"| W4
    FinalPr -->|"no"| Open
    Done -->|"no"| First
    First -->|"yes"| W1
    First -->|"no"| InProg
    InProg -->|"yes"| W2
    InProg -->|"no"| Simple
    Simple -->|"no"| W3
    Simple -->|"yes"| Start
```

*Source: `Core/SubIssueSequencer.cs:18-42`. Sub-issue 1 is always started by a human (or by the breakdown when `autoStartFirstSubIssue: true`).*

## Business rules and edge cases

- **Analysis rounds**: capped by `limits.maxAnalysisRounds` (5). At the cap the job fails on purpose with advice to clarify in a call.
- **Max 7 questions**, Given/When/Then criteria, "simple" = up to 5 production files (test files, docs, lock files don't count). These live in the agent file (`agents/grill-issue.agent.md`), not in C#.
- **Breakdown table is strict**: exactly 6 cells (`# | Component | Title | Scope | Touches | Depends on`), rows numbered 1..N in order, `Depends on` may only name earlier rows, title/scope not empty, `Shared contract:` line required (`BreakdownParser.cs`). Violations fail the analysis job (a ready complex analysis is checked before it is posted) with the exact reason. If an unreadable breakdown still reaches `ai-breakdown`, the job comments the reason, removes the label and asks for a new analysis (`@ai`); it does not add `ai:failed`.
- **`[developer]` prefix** in a title: that sub-issue does **not** get `ai:ready`/`ai:simple`. The body tells a human to build it. The sequencer then waits at it ("waits for a developer") until someone closes it (a PR counts only from branch `ai/issue-<p>-<o>` into `feat/issue-<p>`).
- **Sub-issue body header** (`Parent`, `Order`, `Base branch`, `Depends on`) is what the implement job reads (`SubIssueBody.Parse`). A body with `Parent:` but no `Order:` throws `FormatException`.
- **Final PR** is opened once; the parent comment and PR body list each sub-issue PR. A sub-issue closed without a runner PR is shown as "closed without a runner PR".
- **Sub-issues that stop**: a too-big implement run, or a sub-issue PR closed without a merge, leaves the sub-issue with `ai:failed` and a comment. The sequencer waits until a human removes `ai:failed` (or builds it by hand and closes it).

## Dependencies

- Uses: `IGitBucketClient`, `IWorkspace`, `IGitClient`, `ICopilotRunner`, `IStateStore`, Core parsers.
- Used by: `JobWorker` (via `JobFactory`). Implement reads the parent's hidden list for ordering ([implement](implement.md)).

## Gotchas

- `BreakdownIssueJob` finds existing sub-issues with `ListOpenIssuesAsync(label ai:sub-issue)` across the **whole repo** (not per parent), then filters by body. Closed sub-issues are not in that list, so a retry after closing one would create a duplicate for that order.
- The parent's list is stored in a **hidden comment line**. If a human edits or the bot comment is lost, `SequenceSubIssuesJob` throws "no sub-issue list" and the parent gets `ai:failed`.
- `SequenceSubIssuesJob` is planned on **every poll** for every `ai:parent` issue and costs several API calls each time (issue, comments, each sub-issue, open + closed PR lists). The job then usually ends as Quiet. See [06-findings](../06-findings.md).
- The analysis SPEC is a **separate comment**, posted after the analysis comment. If the SPEC post fails, the analysis comment and labels are already in place (no rollback).
- An analysis posted for a `ready` status but with no SPEC block fails the job *before* posting (`RequireBlock`), so that case is clean.
