# Dashboard UI guide

The rules every screen of the ai-runner dashboard follows. Read this before you build or change a screen, and reuse
what is here before you create anything new. If you add a shared piece, add it to this file in the same change.

Stack: Angular 22 (standalone components, signals, zoneless, OnPush by default), Angular Material 3, Vitest.
The dashboard is **read-only** and **local** (served by the runner on `127.0.0.1`).

## 1. Ground rules

1. **Angular only displays.** The API sends every decision ready to show: a stable `code` for styling plus a `label`
   for text (`state` / `stateLabel`, later `stage` / `stageLabel`, waiting-on text, cost totals). Never read GitBucket
   labels, never compute a stage, a "waiting on" action, a health state or a total. If a screen needs something the
   API does not send, stop and report what the API should add.
2. **Generated client only.** Call the API through `Api` and the functions in `src/app/api/fn/` (generated from the
   runner's OpenAPI document; never edit `src/app/api/`). No `fetch`, no `HttpClient` with a hand-written URL.
   ```ts
   private readonly api = inject(Api);
   readonly jobs = pollResource(() => this.api.invoke(getJobs, { page: 1 }));
   ```
3. **Every data panel handles loading, empty and error** (section 6) and works at 360 px wide (section 4).
4. **Refresh by polling** with `pollResource` (section 7). No websockets, no `setInterval` in components.
5. **Times are UTC ISO-8601 from the API, shown in local time** with `<app-time>`; durations with `| duration`.
6. **No new colors.** Status colors are the six tones (section 2); everything else is a `--mat-sys-*` token.
7. **A new component gets a unit test** (`*.spec.ts` next to it). Run `npm test` and `npm run build` before you finish.

## 2. Theme tokens

Defined in `src/styles.scss`. Light or dark follows the operating system (`color-scheme: light dark`); every token
below works in both, so never hard-code a color.

### Material system tokens (from `mat.theme`, primary azure, tertiary violet)

| Use | Token |
|---|---|
| Page background / text | `--mat-sys-surface`, `--mat-sys-on-surface` |
| Secondary text | `--mat-sys-on-surface-variant` (or the `.app-muted` class) |
| Card background | `--mat-sys-surface-container-low` (the `.app-card` class sets it) |
| Hover on a row or item | `--mat-sys-surface-container-high` |
| Borders and dividers | `--mat-sys-outline-variant` |
| Links, focus ring, accents | `--mat-sys-primary` |
| Errors (text, icons) | `--mat-sys-error` |

### Tones: the only status colors

`export type Tone = 'neutral' | 'info' | 'progress' | 'success' | 'warning' | 'danger'` (`src/app/shared/tone.ts`).
Each tone has three tokens: `--app-tone-<tone>-bg` (soft background), `--app-tone-<tone>-fg` (text on that
background) and `--app-tone-<tone>-dot` (solid dot or icon).

| Tone | Means | Examples |
|---|---|---|
| `neutral` | nothing to say, or not started | stage New, Closed (no merge), unknown codes |
| `info` | ready, waiting for a go-ahead or a review | Ready - simple/complex, PR open, starting |
| `progress` | the runner is working on it | Implementing, Broken down, Running, Queued |
| `success` | finished well | Done, Merged, healthy runner, job ok |
| `warning` | a person must act, or data is late | Questions open, stale health, stale data banner |
| `danger` | failed or broken | Failed flag, job failed, poll errors, runner not reachable |

Map an API code to a tone with a `Record<string, Tone>` in `src/app/shared/tone.ts` (`STAGE_TONES`, `HEALTH_TONES`,
`JOB_STATUS_TONES`, `ISSUE_MARK_TONES`, `ISSUE_MARK_ICONS`, `WORKER_ICONS`) and read it through its helper
(`stageTone(code)`, `healthTone(code)`, `jobStatusTone(code)`, `issueMarkTone(code)`, `issueMarkIcon(code)`,
`workerIcon(code)`), which falls back to `neutral` (or no icon) for an unknown code. Those maps are the only place a code is interpreted: a template never compares
a code (`state === 'busy'`) and never builds label text (plurals, counts). If text is missing, the API should send it
ready (like `errorsLabel` on `/api/health`).

In a component's SCSS, give the host the tone variables with the shared mixin and style from your own variables:
```scss
@use '../../shared/tones';
:host { background: var(--chip-bg); color: var(--chip-fg); }
@include tones.host-variants(chip);   // sets --chip-bg / --chip-fg / --chip-dot from the class tone-<tone> on the host
```

### Spacing, shape, type

- Spacing: `--app-space-1..6` = 4, 8, 12, 16, 24, 32 px. Use them for every gap, padding and margin.
  Inside a card: 16 (`--app-space-4`). Between cards: 16. Page padding: 24 (16 on phones). Below the page header: 24.
- Sizes are not spacing: the menu width is `--app-nav-width`, table column widths are fixed CSS widths in the
  `DataColumn` (`width: '180px'`), dots and icons use the sizes in section 3. Everything that is a gap, padding or
  margin uses the spacing tokens; do not set `min-height` to fake spacing, pad instead.
- Radius: `--app-radius` (12 px, cards), `--app-radius-small` (8 px, banners, small boxes). Chips are pills.
- Content width: `--app-content-max-width` (1280 px), centered.
- Font: the system UI font (no web font download). Monospace: `--app-font-mono` or the `.app-mono` class
  (paths, SHAs, branch names, log text).
- Typography: use the Material type roles only, as `font: var(--mat-sys-<role>)` (a role sets size, weight and line
  height together: no `font-weight`, `font-size` or `line-height` of your own):

| Role | Use |
|---|---|
| `headline-small` | page title (the page header sets it) |
| `title-medium` | card title (`.app-card-title`) |
| `title-small` | empty/error state title |
| `body-medium` | default text |
| `body-small` | dense lines: health strip, banners, table footnotes |
| `label-large` | labels of facts (`.app-facts dt`), strong short labels |
| `label-medium` | chips |

## 3. Icons

`<mat-icon>name</mat-icon>` with a [Material Symbols](https://fonts.google.com/icons) (outlined) name. The font is
bundled (`material-symbols` package), so icons work offline; never link a font CDN. Decorative icons get
`aria-hidden="true"`; an icon-only button gets an `aria-label`. Sizes: 24 px default, 18 px inside text lines,
16 px inside chips.

## 4. Layout

```
+--------------------------------------------------------------+
| toolbar: [menu]  ai-runner   dashboard - read only           |  app.html
+--------------------------------------------------------------+
| health strip (every screen, tone colored)                    |  layout/health-strip
+-----------+--------------------------------------------------+
| side menu | <main class="content">  (max 1280 px, centered) |
| Overview  |   <app-page-header>                              |
| Issues    |   <div class="app-stack">                        |
| Jobs      |     <section class="app-card"> ... </section>    |
| Cost      |     <section class="app-card"> ... </section>    |
+-----------+--------------------------------------------------+
```

- The toolbar and the health strip never scroll; the page scrolls below them.
- At 900 px and wider the menu stands beside the page; below it slides over the page (menu button in the toolbar).
  The breakpoint is `NARROW_QUERY` in `app.ts`. Inside screens use the same two widths: `599.98px` (phone) and
  `899.98px` (tablet) in `@media (max-width: ...)`.
- The menu entries are `NAV_ITEMS` in `layout/nav-items.ts`. A screen with a parameter (issue detail) is not a menu
  entry; it is reached by clicking a row.
- Routes are in `app.routes.ts`, one `loadComponent` per screen, title `"<Screen> - ai-runner"`. A screen that is
  not built yet uses `PlaceholderPage` with `data: { heading, subtitle }`; replace it with the real component.
- A screen is: `<app-page-header>` then `<div class="app-stack">` with one `<section class="app-card">` per panel,
  each with an `<h2 class="app-card-title">`. Side by side panels: a CSS grid inside `.app-stack` that falls back to
  one column below 900 px.
- **No horizontal page scroll at any width.** Wide tables scroll inside their own box (the data table does this);
  long text breaks with `overflow-wrap: anywhere` (`.app-mono` does this).

## 5. Shared components (`src/app/shared/`)

| Piece | Use it for | Do not use it for |
|---|---|---|
| `app-page-header` | the title of every screen, filters/toggles on the right | card titles |
| `app-data-table` + `appCell` | every list of rows from the API | a list of 2-3 facts (use `.app-facts`) |
| `app-status-chip` | a state from the API: flags, badges, outcomes | buttons or links |
| `app-stage-chip` | the workflow stage of an issue | anything else |
| `app-view-state` | loading / empty / error of a panel that is not a table | inline hints |
| `app-inline-note` | one tone colored line inside a panel: stale data, a cut file | a whole-panel state |
| `app-markdown` | Markdown text from the API (log files), rendered safely | text that is not Markdown (use `<pre class="app-mono">`) |
| `app-time` | every point in time | durations |
| `duration` pipe | every duration in seconds | points in time |
| `pollResource` | every API read | one-off actions |
| `.app-card`, `.app-stack`, `.app-facts`, `.app-muted`, `.app-mono` | page structure and text (global CSS) | |

### `app-page-header`

```html
<app-page-header title="Jobs" subtitle="Job history with outcome, duration and logs.">
  <mat-button-toggle-group actions>...</mat-button-toggle-group>
</app-page-header>
```
One per screen, first element. Title is the menu label (or the item's name on a detail screen). Subtitle is one
short sentence. Put filters and toggles in the `actions` slot; they wrap below the title on narrow screens.

### `app-data-table`

```html
<app-data-table
  [rows]="jobs.value()?.items"
  [columns]="columns"
  [loading]="jobs.isLoading()"
  [error]="jobs.error()"
  [trackBy]="trackById"
  [clickable]="true"
  (rowClick)="open($event)"
  (retry)="jobs.reload()"
  emptyTitle="No jobs yet"
  emptyMessage="Jobs appear here once the runner has run one."
>
  <ng-template appCell="stage" let-row>
    <app-stage-chip [code]="row.stage" [label]="row.stageLabel" />
  </ng-template>
  <ng-template appCell="startedAt" let-row><app-time [value]="row.startedAt" /></ng-template>
</app-data-table>
```
```ts
protected readonly columns: readonly DataColumn[] = [
  { key: 'title', header: 'Issue' },
  { key: 'stage', header: 'Stage', width: '160px' },
  { key: 'duration', header: 'Duration', align: 'end', hideBelow: 'sm' },
];
protected readonly trackById = (_index: number, row: JobRow) => row.id;
```
- A column without an `appCell` template shows `row[key]` as text (`-` for null).
- Counts that can grow large (calls, tokens, billed credits) go through Angular's `| number` pipe (`DecimalPipe`) for
  thousands separators; in TypeScript use `formatNumber(value, inject(LOCALE_ID), '1.0-0')`. Format only, never
  convert units.
- `align: 'end'` for numbers and durations. `hideBelow: 'sm' | 'md'` drops less important columns on small screens;
  never hide the column that identifies the row.
- Always pass `trackBy` with a stable id, so a 3 s refresh does not rebuild rows.
- It shows the loading state before the first answer, the error state (with "Try again" -> `retry`) when there is
  no data, the empty state for an empty list, and a "Showing older data" banner when a refresh fails but rows exist.
- `clickable` + `rowClick` to open a detail screen (keyboard: Enter works). Links inside cells are fine too.
- Paging (when an endpoint pages): put a `mat-paginator` under the table and pass the page to the endpoint; the table
  itself does not page or sort on the client.
- Nested rows (sub-issues under a parent): the table is flat. Turn the API's nested answer into lines (each parent
  followed by its children, with a flag such as `isSubIssue`) in a `computed`, and indent the first cell's template
  with padding (`pages/issues`). That is layout only: the API decides what is nested and what matches a filter.

### `app-status-chip`

```html
<app-status-chip label="Failed" tone="danger" />
<app-status-chip label="Claimed" tone="neutral" icon="lock" title="Claimed by laptop-2" />
```
`label` comes from the API. `tone` from a code-to-tone map (section 2). Optional `icon` (replaces the dot) and
`title` (hover text). Flags next to a stage: `Failed` danger, `Claimed by X` neutral with `lock`, `On GitHub`
neutral with `cloud`. Badges: `Queued` progress with `schedule`, `Running` progress with `play_circle`. Flags and
badges of an issue (codes `failed`, `claimed`, `on-github`, `queued`, `running`) take both from `issueMarkTone` and
`issueMarkIcon`:
```html
<app-status-chip [label]="flag.label" [tone]="markTone(flag.code)" [icon]="markIcon(flag.code)" />
```

### `app-stage-chip`

```html
<app-stage-chip [code]="row.stage" [label]="row.stageLabel" />
```
Colors come from `STAGE_TONES` in `shared/tone.ts`, keyed by the stage codes of `docs/specs/dashboard.md`:
`new`, `questions-open`, `ready-simple`, `ready-complex`, `implementing`, `broken-down`, `pr-open`, `merged`,
`done`, `closed-no-merge`. If the API uses a different code, change the map and its test together with the API.
The label is shown as sent ("Broken down (2/5)").

### `app-view-state`

```html
<app-view-state kind="loading" />
<app-view-state kind="empty" title="No poll errors" message="The last poll of every project went through." />
<app-view-state kind="error" [message]="resource.error()" [canRetry]="true" (retry)="resource.reload()" />
```
Optional `icon` (Material Symbols name). Use it inside a card for a panel that is not a table, or for a whole
screen before its first answer.

### `app-inline-note`

```html
<app-inline-note icon="sync_problem">Showing older data. {{ message }}</app-inline-note>
<app-inline-note>{{ file.truncatedNote }}</app-inline-note>          <!-- tone warning, icon warning by default -->
```
Optional `tone` (default `warning`) and `icon`. Use it above a panel's data when a later call failed but the data
stays ("Showing older data. ..."), or when the API sends a note about the data (a cut log file). The data table has
its own banner for the same case.

### `app-markdown`

```html
<app-markdown [text]="file.text" />
```
Renders Markdown with `markdown-it` and `html: false`: raw HTML in the text is shown as text, never as an element,
and `javascript:`/`data:` links stay plain text. Angular sanitizes the result again on `innerHTML`. Never pass its
output through `bypassSecurityTrustHtml`, and never bind log or agent text to `innerHTML` any other way. Plain text
logs go in `<pre class="app-mono">` with `white-space: pre-wrap`, so they wrap instead of scrolling the page.

### `app-time` and the `duration` pipe

```html
<app-time [value]="row.startedAt" />                 <!-- 14:03:12 today, "19 Oct 2026, 14:03:12" otherwise -->
<app-time [value]="health.lastPollEndedAt" mode="relative" />   <!-- "30 s ago" -->
{{ row.durationSeconds | duration }}                  <!-- "3 min 05 s"; "-" for null (old rows) -->
```
`app-time` has a tooltip with the full local date and time, shows `-` for null, and updates relative text every
second from the shared `Clock`. Use `relative` where recency matters (health, "last seen"), `absolute` in tables and
facts. Never format dates with `DatePipe` or `toLocaleString` directly.

### `pollResource`

```ts
private readonly api = inject(Api);
protected readonly overview = pollResource(() => this.api.invoke(getOverview));
// template: overview.value(), overview.error(), overview.isLoading(), overview.reload()
```
Polls every 3 s (`POLL_INTERVAL_MS`) while the tab is visible, waits for each answer before the next call, pauses
while the tab is hidden, loads at once when it is visible again, and stops when the component is destroyed. Create
it in a field initializer. With parameters (filters, a route id), create it from a function that reads signals:
```ts
protected readonly issues = pollResource(() => this.api.invoke(getIssues, { project: this.project() }));
// call issues.reload() when a filter changes, so the new filter is used at once
```
`reload()` during a running call loads once more right after it, so a changed filter is never left with the old
answer. `value` keeps the last good answer when a call fails; `error` holds the failure text until the next success
(for a 400 the API's own `ApiError.message`). Prefer filter options the API sends (`/api/issues` sends its projects,
components and stages). Options the API cannot list live in one constant per filter next to the screen
(`pages/jobs/job-filters.ts`, `pages/issues/issue-filters.ts`), typed with `FilterOption` from `shared/filter-option.ts`.

### Health

The health is polled once for the whole app by `HealthStore` (`layout/health-store.ts`). A screen that shows health
data injects `HealthStore` and reads `health.value()`; it never polls `/api/health` itself. Health codes map to
tones in `HEALTH_TONES` (`shared/tone.ts`, use `healthTone(code)`).

### Charts

No chart library. The one chart so far, `pages/cost/cost-chart` (bars of what Copilot billed, `nanoAiu` as sent:
columns over days, horizontal bars per project, issue or model), is inline SVG measured to its card's width, so
text never scales. It only turns the API's
numbers into pixels (no sums, no reordering), uses `--mat-sys-primary` for marks and `--mat-sys-outline-variant`
hairlines, has `role="img"` with `<title>`/`<desc>`, a tooltip per bar, and the data table under it is its text
alternative. Follow the same rules for a new chart and move it to `shared/` once a second screen needs it.

### Global CSS classes (`src/styles.scss`)

- `.app-card` + `.app-card-title`: a panel with a title.
- `.app-stack`: vertical list of cards with the standard gap.
- `.app-facts` on a `<dl>`: label/value pairs in two columns (one on phones).
- `.app-muted`: secondary text. `.app-mono`: code-like text that wraps.
- `matTooltipClass="app-pre-line"`: a tooltip that keeps line breaks.

## 6. Loading, empty and error states

| Situation | Show |
|---|---|
| no answer yet (`isLoading()`) | `app-view-state kind="loading"` in the panel (the table does it itself) |
| answer with no items | empty state: a short title saying what is missing ("No jobs yet") and one sentence on when items appear |
| first call failed | error state with the message from `error()` and "Try again" (`reload()`) |
| later call failed, data on screen | keep the data; the table shows a warning banner; elsewhere a small `warning` tone line |
| runner not reachable | the health strip says so on every screen; panels show their error state |

Write state text for the user, in plain words: what is shown, what it means, what to do. No "Error 0" or stack text.

## 7. Refresh

Each screen polls its own endpoint every 3 s while visible, with `pollResource`. Do not add refresh buttons for
normal use (the "Try again" of an error state calls `reload()`). Avoid layout jumps on refresh: track rows by id,
keep data on screen while reloading, never show the loading state again after the first answer.

## 8. Accessibility

- One `h1` per screen (the page header), `h2` per card; `aria-labelledby` from the `section` to its `h2`.
- Color is never the only signal: every chip has text, every state has a label.
- Clickable rows and buttons are reachable by keyboard and show the focus ring (`--mat-sys-primary`).
- Live areas: the health strip is `role="status"`; error states are `role="alert"` (the components set this).

## 9. Tests

Vitest through `ng test` (`npm test` runs once, `npm run test:watch` watches). Test what the user sees: text,
tone classes, rows, states. Test helpers in `src/app/shared/testing.ts`: `fakePollResource(value)` (set `value`,
`error`, `isLoading` by hand), `fixedClock` (now = 2026-01-01 12:00 UTC), `sampleHealth(overrides)`. Provide a
fake store with `{ provide: HealthStore, useValue: { health: fakePollResource(...) } }`. For a screen that polls
its own endpoint provide `FakeApi` (`{ provide: Api, useValue: new FakeApi().answer(getJobs, (params) => page) }`;
`callsOf(getJobFile)` returns the parameters of every call), build rows with `sampleJob(overrides)` or
`sampleIssue(overrides)`, and wait for the
answers with `await settle(fixture)`. Material controls are driven with their harnesses (`MatSelectHarness`,
`MatPaginatorHarness`). For a test of the HTTP layer itself use `provideHttpClient()` + `provideHttpClientTesting()`
and `HttpTestingController`.

## 10. Commands (in `src/AiRunner.Dashboard/`)

| Command | Does |
|---|---|
| `npm start` | dev server on http://localhost:4200, `/api` proxied to the runner on `127.0.0.1:5180` (`proxy.conf.json`) |
| `npm test` | unit tests once |
| `npm run build` | production build into `dist/` |
| `npm run generate:api` | regenerate `src/app/api/` from `openapi.json` |
| `../../build-dashboard.ps1` | everything: OpenAPI document, `npm ci`, client, tests, build, copy to the runner's `wwwroot/` |

After an API change in C#: run `build-dashboard.ps1` (or `dotnet run --project ../AiRunner -- --openapi <abs path to
openapi.json>` then `npm run generate:api`), commit `openapi.json` and `src/app/api/` with the change.
