# ai-runner dashboard (UI)

The front end of the runner's local, read-only dashboard. The runner serves the built files and the API
(`/api`, OpenAPI at `/openapi/v1.json`) on `http://localhost:<dashboard.port>/` when `dashboard.enabled: true`.

- Design and rules for every screen: [UI.md](UI.md).
- What the dashboard shows and why: `docs/specs/dashboard.md`, `docs/adr/0001`-`0003`.
- Build and install into the runner: `./build-dashboard.ps1` in the repository root.
- Develop: `npm start` (needs a runner with the dashboard enabled on port 5180 for live data).
- The API client in `src/app/api/` is generated from `openapi.json` (`npm run generate:api`); do not edit it.
