---
name: grill-me
description: Method to question a feature request or issue until it is clear enough to build. Finds ambiguity, hidden decisions and missing data before any code is written. Use when analyzing an issue.
---

# Grill me: question a request before building it

The goal is to find every decision the author has not made yet, and to ask about it once, clearly.
A good question saves a wrong implementation. A useless question wastes the author's time.

## 1. Read like a tester, not like a fan
Read the request and the thread twice. For each sentence ask: "Could two developers build two different things from this?"
If yes, that is a question or an assumption.

## 2. Check these areas, in this order
1. **Who**: which users or roles? Who must NOT be able to do it?
2. **What exactly**: inputs, outputs, fields, formats (date, number, currency, encoding, file type), sizes and limits.
3. **When**: triggers, timing, order of steps, what happens if it runs twice.
4. **Where**: which screen, endpoint, job, service, environment. New place or existing one?
5. **Rules**: business rules, validation, rounding, sorting, default values.
6. **Errors**: what the user sees when input is wrong, when a dependency is down, when there is no data.
7. **Data**: new fields or tables? Migration of old data? Who owns the data?
8. **Existing behaviour**: what must stay the same? Who else uses this code?
9. **Done**: how will the author check it works? (This becomes the acceptance criteria.)
10. **Out of scope**: what the author does NOT want now.

## 3. Ask the code before you ask the human
Search the repository for the answer first: similar features, existing enums, validators, config, tests.
- If the code answers it clearly, write an **assumption** (`A1: ... (because: <file>)`), not a question.
- If the code shows two possible patterns, ask which one, and name both files.

## 4. Write good questions
- One sentence. One decision per question. Numbered `Q1`..`Q7`, max 7.
- Add `(why I ask: ...)` in a few words: what goes wrong if we guess.
- Offer options when you can: "CSV or Excel?" is easier to answer than "Which format?".
- Most important first. If you have more than 7, keep the 7 that change the design most; turn the rest into assumptions.
- Never ask what the thread already answers. Never ask again with other words.

## 5. Write good assumptions
- Numbered `A1`..`An`, one sentence each, with the reason.
- Make them easy to reject: "A2: Only admins can export. (because: ReportController has [Authorize(Roles = Admin)])".

## 6. Decide the status honestly
- `ready` only when every question you asked has an answer and nothing important is left to guess.
- Unclear answers ("maybe", "as usual") are not answers: ask again, more concretely, and say what was unclear.

## Style
Simple English. Short sentences. No marketing words. The readers are not native English speakers.
