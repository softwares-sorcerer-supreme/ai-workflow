---
name: implement-checklist
description: The team's rules for writing a change. Use when implementing an issue. A starting point; each team edits it (or adds its own copy in .github/skills/implement-checklist/).
---

# Implement checklist

Go through this before you leave each stage of the implement-issue agent.

## Before you write code
- Do you know every file you will change, and why? Is each one in plan.md?
- Is there an existing helper, base class or pattern for this? Reuse it instead of writing a new one.
- Did you open the `Pattern from` file and one neighbour of the same kind? Match their naming, layout, error handling, logging, dependency handling and test style. A convention of the neighbours beats your own habit.
- Which test file covers this code today? Add your tests next to the existing ones.

## Correctness
- Every acceptance criterion in plan.md is implemented and has a test.
- Edge cases: empty input, null / missing values, one item, many items, duplicates, boundaries.
- Errors are handled the way AGENTS.md describes (no swallowed exceptions, clear messages).
- Existing callers of a changed function still work.

## Often missed (each one came back from a pull request review and cost a fix round)
- Async state: a slower, older request or timer must not overwrite the result of a newer one (keep a request id or cancel the old one); a debounce clears its pending timer before any early return; overlapping loads do not end each other's loading state.
- Failure paths can recover: after a failed load the user can try again, and the screen does not look like a valid empty or unfiltered result.
- Saved state (browser storage, URL, cache, files) can hold ids that no longer exist: check it against fresh data before it is used.
- No default value that hides a bug (a made-up value for a required field): fail or validate instead.
- Do not change shared production code or unrelated files (comments, blank lines included) to make a test easier. Follow the test layout the earlier changes of the same feature used; put common test setup in one helper instead of copying it into many files.
- PR_BODY.md describes the diff as it is (no file it does not contain).

## Tests
- Tests check behaviour, not implementation details.
- There is a test for the main error path.
- Never delete, skip or weaken a test to make a check pass.

## Simplicity
- The simplest code that meets the plan. No option, flag, layer or interface the plan does not need.
- No copy-paste of logic that already exists; no dead code, unused parameters or commented-out code.
- Names follow AGENTS.md and the code around the change.
- No unrelated reformatting or renaming: keep the diff small.

## Verify
- Build, test and lint (from AGENTS.md) run in full and pass.
- PR_BODY.md says exactly what you ran and what happened.

## Team rules
Add your own rules here, for example:
- "All new endpoints need an integration test."
- "Use the shared Result type for errors, not exceptions, in the Application layer."
