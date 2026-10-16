---
name: ui-checklist
description: Rules for building or reviewing a user interface so that a new page looks like the pages around it. Use when a change has templates, styles or components, and the target component's AGENTS.md has a "UI conventions" section.
---

# UI checklist

A new screen must look and behave like the screens next to it. The design system and the existing pages decide, not your taste.
Everything product-specific (file names, component names, tokens) comes from the `## UI conventions` section of AGENTS.md and from the SPEC's `### UI changes` table in plan.md. This list only says what to check.
You have no browser, no `git` and no `grep`: check by reading the files you changed.

## Before you write
- Open the design-system document that AGENTS.md names, and read the sections it lists. If a per-page override exists, it wins.
- Open the `Copy from` page of the `### UI changes` row (or the page AGENTS.md names for this kind of screen). Note its layout shell, its shared components, its states and how it is laid out on a small screen.
- List the shared components you will use. Do not rebuild one that exists.

## While you write
- Draw the page inside the layout shell, like the page you copy from.
- Use the tokens or variables of the design system for every colour, space, size and font. No literal values, no inline styles, no new global styles.
- Show all three states of every block that loads data: loading, empty and error (with a way to retry when the shared component offers it). Data is the fourth state.
- Match the copy-from page on a small screen: same breakpoints, same wrapping, no fixed widths that break the layout.
- Accessibility basics: every input has a label, every interactive element can be reached and shows focus, colour is never the only signal, text meets the contrast rule of the design system.
- Forms and validation messages follow the pattern of the copy-from form.

## Before you leave
- A DOM test per new page or component renders its loading, empty, error and data states.
- Read every template and style file you changed and look for literal colours (hex, rgb, named colours), literal pixel or rem values that are not tokens, and font names. Replace each with the token. No new colours, spacing values or fonts.
- Compare the result with the copy-from page once more: same shell, same shared components, same states.
- Anything the design system does not cover goes under "Open points" in PR_BODY.md, not into a private style.

## In a review
A violation of any line above is a `convention` finding. The fix names the token, the shared component or the copy-from file to use.
