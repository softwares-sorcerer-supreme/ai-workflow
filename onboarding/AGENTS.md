# AGENTS.md

<!--
Starting point for a product repo. Copy this file to the repo root and fill it in.
Keep it short: Copilot loads it on every run and it costs context.
Delete these comments and the examples you do not need.
-->

## Build and test
The agent runs these before it stops (it may also run the same tools with other arguments, e.g. one test),
and ai-runner runs build, test and lint again after the agent stops and sends failures back to it.
They must be the same as `commands.setup`, `commands.build`, `commands.test` and `commands.lint` for this repo in `repos.yml`.

- Setup: `<install dependencies, or delete this line>`
- Build: `<build command>`
- Test: `<test command>`
- Lint: `<lint or format check, or delete this line>`

<!--
Examples:
  .NET:    Build: `dotnet build MyService.sln`      Test: `dotnet test MyService.sln --no-build`
  Node:    Setup: `npm ci`  Build: `npm run build`  Test: `npm test`  Lint: `npm run lint`
  Java:    Build: `mvn -q -DskipTests package`      Test: `mvn -q test`
  Gradle:  Build: `./gradlew assemble`              Test: `./gradlew test`
  Python:  Setup: `pip install -r requirements.txt`  Build: `python -m compileall -q src`  Test: `pytest -q`
  Go:      Build: `go build ./...`                  Test: `go test ./...`
The work folder is cleaned before every job (git clean -fdx), so node_modules, .venv and
build output are gone: put the install step in Setup. Setup runs as a normal process of the
runner (with network); the agent itself has no network besides the Copilot service.
-->

## Folder map
Where things live, so the agent does not search the whole repo.

- `<folder>`: <what is in it>
- `<folder>`: <what is in it>
- Tests: `<folder>` (one test file per <class / module>, named `<pattern>`)

## Conventions
- Architecture: <layers or modules, and which may call which>
- Naming: <files, classes, functions, tests>
- Error handling: <how errors are returned and logged>
- Data access: <ORM / queries / repositories pattern>
- <anything a new developer usually gets wrong>

## Never edit
- `<migrations folder>`
- `<secrets / config with credentials>`
- `<CI files, e.g. Jenkinsfile>`
- `.github/`

## Copy from here
One or two good examples of a complete feature to copy the pattern from:
- `<path>`: <what it shows>

## Review
Files a review can skip (generated code, lock files, docs). Also put them in `review.skipPaths` in `repos.yml`.
- `<glob>`
