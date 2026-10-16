<!-- SPEC_START -->
## SPEC - issue #{n}: {title}

### Goal
One or two sentences.

### In scope
- ...

### Out of scope
- ...

### Acceptance criteria
1. Given ..., when ..., then ...
2. ...

### Technical plan
| Step | Component | File | Pattern from | Change |
| --- | --- | --- | --- | --- |
| 1 | <one component name> | `path/relative/to/the/component/folder` | `existing/file/to/copy` | <what changes> |
| 2 | ... | ... | ... | ... |
(Pattern from: the existing file, relative to the component folder, whose structure the new or changed file must follow: naming, layout, error handling, tests. Write `-` only when no file of that kind exists, and then say so in Change.)

### Data changes
- Entity / table / schema changes, or None. Migrations: yes / no.

### API changes
One row per endpoint that is new or changes, or None. Decide paging, filtering, sorting, errors and auth for every one.
| Method | Path | Query / paging | Request | Response | Errors | Auth |
| --- | --- | --- | --- | --- | --- | --- |
| <METHOD> | </path> | <query params, paging, sorting or -> | <request type or -> | <response type> | <status codes> | <permission or -> |

### UI changes
One row per page or component that is new or changes, or None. Paths are relative to the folder of the component that builds the row.
| Route | Page / component | Copy from | Shared components | States |
| --- | --- | --- | --- | --- |
| <route or -> | <file of the page or component> | <existing page or component to copy> | <shared components to use> | <loading, empty, error, data> |

### Shared contract (exact names)
- Types / classes: ...
- Data shapes (DTOs, JSON): ...
- Endpoints: ...
- Enums / constants: ...

### Tests to write
- Unit: ...
- Integration: ...

### Open points (decided by assumption, confirmed in the thread)
- ...
<!-- SPEC_END -->
