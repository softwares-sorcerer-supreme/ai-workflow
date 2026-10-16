---
name: review-checklist
description: The team's rules for code review. Use when reviewing a pull request. A starting point; each team edits it (or adds its own copy in .github/skills/review-checklist/).
---

# Review checklist

Check these in order. Report only real problems in the diff, with a concrete fix.

## Correctness (severity: bug)
- Does the change do what the PR description and the linked SPEC say?
- Edge cases: empty input, null / missing values, one item, many items, duplicates, boundaries (0, max, last day of month, time zones).
- Is existing behaviour changed by accident (other callers of a changed function)?
- Async code: missing await, race conditions, shared state.

## Risk (severity: risk)
- **Security**: user input validated? Authorization checked on new endpoints? No SQL built from strings? No secrets, tokens or connection strings in code or config?
- **Data**: can this lose or corrupt data? Transactions where several writes must succeed together? Migrations safe on existing data?
- **Errors**: exceptions caught at the right level, logged with context, not swallowed? Clear message for the user?
- **Performance**: queries inside loops, loading full tables, missing paging, large files read into memory.
- **Compatibility**: public API, message or file formats changed without a version or migration?

## Convention (severity: convention)
Open the `Pattern from` file of the plan (if there is one) and one neighbour of the same kind next to each changed file. Report a finding when the change differs from them in:
- naming of files, types, functions, tests and routes;
- file and folder layout (where the file lives, one type per file or not);
- structure (the order and shape of the parts, the base class or helper they use);
- error handling and logging;
- how dependencies are obtained, and how data is accessed;
- test style (framework, naming, setup).
The fix names the neighbour file and line to copy. A rule written in AGENTS.md that the change breaks is a convention finding too.
When the change has templates or styles, also use the ui-checklist skill; a violation is a convention finding.

## Tests
- Is every acceptance criterion covered by a test?
- Do the tests check behaviour, not implementation details?
- Is there a test for the main error path?

## Style (severity: style)
- The change is in the right layer / folder (see the folder map in AGENTS.md).
- No copy-paste of logic that already exists; no dead code; no commented-out code.

## Simplicity (severity: style; the code-reviewer subagent calls it simplify)
- An existing helper, base class or library call already does this: reuse it.
- An abstraction, option, flag or layer that the change does not need.
- Code that can be clearly shorter without losing clarity (nested ifs, manual loops a built-in covers).
- Unrelated reformatting or renaming that makes the diff bigger.

## Team rules
Add your own rules here, for example:
- "All new endpoints need an integration test."
- "Use the shared Result type for errors, not exceptions, in the Application layer."
