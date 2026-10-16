<!--
EXAMPLE for the sample project "mbh", component `frontend` (repo mbhanalytics-fe).
Not used by the runner. Copy it to the root of that repo as AGENTS.md (the repo has none today).
Sources: the repo's CLAUDE.md, .github/copilot-instructions.md, design-system/mbh-analytics/MASTER.md.
CLAUDE.md can keep its own content or import this file with a line `@AGENTS.md`, so there is one set of rules.
-->
# MBHAnalytics frontend - agent instructions

Angular 21 app for contract financial analytics: standalone components, zoneless, signals, DevExtreme widgets, SCSS, Vitest.
The backend is the API of record. Services call relative `api/...` paths; the real endpoints are in the backend repo (see components.md).

## Build and test
Same commands as `commands` of this component in `repos.yml`.

- Setup: `npm ci`
- Build: `npm run build`
- Test: `npm test` (one file: `ng test --include='**/foo.spec.ts'`)
- Lint: `npm run lint && npx prettier --check .` (type check of app and specs, then formatting)

## Folder map
- `src/app/features/{feature}/`: `models/` (interfaces that match API DTOs), `services/` (API services), `components/` (reusable pieces of the feature, not routed), `pages/` (routed components), `{feature}.routes.ts` (lazy routes).
- A report-style feature with many parts gets a sub-folder `features/{feature}/{sub-feature}/` with page, API service, model and child components together (`features/reliability/chargeback-report/`). Keep it flat.
- `src/app/shared/components/`: components used by several features.
- `src/app/core/`: guards, interceptors, app-wide services, constants.
- Path aliases: `@core/*`, `@shared/*`, `@features/*`, `@env`. Never use deep relative imports (`../../..`) across areas.

## Conventions
- Standalone components, signals (`signal()`, `computed()`, `input()`, `output()`, `inject()`), `@if` / `@for` control flow, Reactive Forms.
- File names have no `.component` part: `foo.ts`, `foo.html`, `foo.scss`, `foo.spec.ts`, class `Foo`. Pages: `login.page.ts`, class `LoginPage`.
- HTTP: the interceptors `apiBaseUrlInterceptor` then `authInterceptor` do the base URL and the token. Services call relative `api/...` paths. Responses are fully typed: the generic matches the model.
- Permissions: `permissionGuard(permission)` on routes, constants in `core/constants/app-permissions.ts`. This is UI only; the API checks again.
- Shared state across two or more components: a plain `@Injectable()` store with signals (see `features/reliability/stores/`). `@ngrx/signals` is not installed; do not add it.
- DevExtreme (`devextreme-angular`): use the declarative `dxo-*` options in templates (for example `<dxo-paging [pageSize]="20">` inside `<dx-data-grid>`), not an options object in TypeScript. Import the module (for example `DxDataGridModule`) in the component. Override widget styles in the component SCSS, `::ng-deep` only when you must.
- Do not add a component that only forwards inputs to another, nor step components plus a store for a form one component can own.
- Tests: Vitest with `TestBed`. A spec file sits next to the file it tests.

## UI conventions
The look of this product is fixed by the design system. Do not use the `frontend-design` skill: it asks for a distinctive design, and a new page must look like the pages next to it.

- Before you write a template or SCSS, open `design-system/mbh-analytics/MASTER.md`: sections 1 Color System, 2 Typography, 3 Spacing & Layout, and the component specification in section 4 of what you build (buttons, KPI cards, status badges, data table, form inputs, modals). Read section 9 (Anti-Patterns) and 10 (Pre-Delivery Checklist). If `design-system/mbh-analytics/pages/<page-name>.md` exists, it wins over MASTER.md.
- Styling: CSS variables only (`--color-primary`, `--color-surface`, `--space-md`, `--text-base`, ...). Never a hard-coded hex colour or pixel value for colour, spacing or type. No inline styles. No new global CSS: put styles in the component SCSS.
- The accent colour is for one highlight per screen and only for large text (3.1:1 contrast); never for small body text. Never red and green alone to show a state: add a label or icon. Light mode only.
- Layout: authenticated pages render inside `main-layout` (sidebar 240px / 64px collapsed, sticky 56px top bar). Do not build another shell.
- Shared components (`src/app/shared/components/`):
  - `main-layout`, `nav-menu`: the shell and the menu. A new page is added to the menu data, not drawn by hand.
  - `async-panel`: the loading / error / empty / data shell for any grid, chart or list. Use it for every block that loads data.
  - `loading-panel`, `api-progress-bar`: global loading overlay and request bar; placed once in the root, do not add more.
  - `confirmation-popup`: every delete or other destructive action.
  - `permission-guard`, `access-denied`: permission-gated routes and their fallback page.
  - `stepper`: multi-step flows.
- Three states: every block that loads data shows loading (skeleton), empty (message) and error (message with Retry). Use `async-panel` (`features/reliability/chargeback-report/chargeback-report.page.html` shows how); do not draw your own.
- Every data screen has at least a date-range filter. Charts always have a legend, a tooltip and a skeleton while loading.
- Forms: Reactive Forms, one validation message per field next to the field, copy the form named under "Copy from here".
- Interactive elements have a visible focus ring and `cursor: pointer`; icons are SVG (Lucide or Heroicons), never emoji.
- Tests: every new page or component gets a DOM test that renders its loading, empty, error and data states (see `shared/components/async-panel/async-panel.spec.ts`).

## Never edit
- `.github/`, `jenkins/`
- `design-system/` (change the design system in its own task, not inside a feature)
- `.agents/`, `public/`
- `angular.json`, `package.json`, `tsconfig*.json`, `src/environments/`

## Copy from here
Open the folder and pick the most complete example of the kind you build.
- API service: `src/app/features/auth/services/auth-api.service.ts`
- Routed page: `src/app/features/auth/pages/login/login.page.ts`
- List or report page with a grid and filters: `src/app/features/reliability/chargeback-report/chargeback-report.page.ts` (with `criteria/` for the filter form)
- Detail page with tabs: `src/app/features/contracts/pages/contract-detail/contract-detail.page.ts`
- Form (create or edit): `src/app/features/contracts/components/contract-upsert/contract-upsert.ts`
- Store: `src/app/features/reliability/stores/mod-studies-wizard.store.ts`
- Interceptor: `src/app/core/interceptors/api-base-url.interceptor.ts`

## Open points
Nobody answers you while you work. When the plan is unclear or a rule here does not fit, do not guess and do not stop: do the part that is clear and write the question under "Open points" in PR_BODY.md.

## Review
Files a review can skip. Also in `review.skip_paths` in `repos.yml`.
- `package-lock.json`
- `**/*.md`
