<!-- REVIEW_START -->
## AI review - commit {short sha}

**Verdict:** looks good | needs changes | needs discussion

### Summary
2-3 sentences: what this PR changes and why, for a reviewer who has not read the issue.

### Findings
| # | Severity | Where | Problem | Suggested fix |
| --- | --- | --- | --- | --- |
| 1 | bug | `path/to/file:42` | <what is wrong> | <concrete fix> |
| 2 | risk | `path/to/file:80` | ... | ... |
| 3 | convention | `path/to/file:10` | <differs from the neighbour file in naming, layout, error handling or tests> | <name the neighbour file and line to copy> |
| 4 | style | ... | ... | ... |

(Severity: bug = wrong behaviour; risk = security, performance, data loss, missing error handling; convention = the change differs from the existing code around it in naming, file or folder layout, structure, error handling, logging, dependency injection, data access or test style; style = other naming or duplication points. A bug, risk or convention finding means the Verdict is needs changes. Max 10 rows; more style points are summarised in one line under the table. No findings = write None instead of the table.)

### Previous findings
(Only when context.md says this is an incremental review: every finding of the previous AI review in context.md, once, with its old number as P<n>. Status is fixed or still open. Otherwise None.)
| # | Status | Where | Problem |
| --- | --- | --- | --- |
| P1 | fixed | `path/to/file:42` | <the old problem, one line> |
| P2 | still open | `path/to/file:80` | ... |

### Spec check
(Only for branches ai/issue-*; otherwise None.)
- [ ] Follows the technical plan in the SPEC, and each file follows its `Pattern from` file
- [ ] Touches only the files listed, or explains why not
- [ ] Uses the shared contract names
- [ ] Acceptance criteria covered: {x of y}

### Tests
- Covered: ...
- Not covered: ...

### Questions for the author
- ...
(None if nothing)
<!-- REVIEW_END -->
