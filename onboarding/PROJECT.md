# PROJECT.md

<!--
Template for the project description. Copy this file into the issues component of the project
(the component named in `issues_in` in repos.yml) and fill it in. The file name is the `context:` key of the project
(default PROJECT.md). The agent reads it first, before components.md, so keep it short and plain.
Delete these comments and the examples you do not need.
-->

## What the project is
<Two or three sentences: who uses it and what it does.>

## Components
One line per component. The names are the ones in repos.yml. components.md (made by the runner for every job) has the folders.

- `<component>`: <what it does, the main language or framework>
- `<component>`: <what it does, the main language or framework>

## How the components talk to each other
<API paths, message names, shared types or files. Say where the contract is written down, so a change on one side can be checked on the other.>

## Where issues go
All issues and sub-issues are filed in `<issues repo>`. Issues in other repos of this project are ignored by ai-runner.

## Words used here
- <term>: <meaning in this project>

## Rules that are not in a component's AGENTS.md
- <for example: every public API change needs a note in docs/api.md>
