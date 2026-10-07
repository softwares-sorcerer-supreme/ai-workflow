<!-- SPEC_START -->
## SPEC – issue #{n}: {title}

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
| Step | File | Change |
| --- | --- | --- |
| 1 | `src/Domain/Entities/Export.cs` | new entity, fields: Id, RequestedBy, Status |
| 2 | `src/Application/Exports/CreateExportCommand.cs` | new command + handler, follows `CreateReportCommand` |
| 3 | ... | ... |

### Data changes
- Entity / table changes, or None. Migrations: yes / no.

### API changes
- `POST /api/exports` → request `CreateExportRequest`, response `ExportDto`. Or None.

### Shared contract (exact names)
- Classes: ...
- DTOs: ...
- Endpoints: ...
- Enums / constants: ...

### Tests to write
- Unit: ...
- Integration: ...

### Open points (decided by assumption, confirmed in the thread)
- ...
<!-- SPEC_END -->