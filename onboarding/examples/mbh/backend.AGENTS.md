<!--
EXAMPLE for the sample project "mbh", component `backend` (repo mbhanalytics-be).
Not used by the runner. Copy it to the root of that repo as AGENTS.md; it replaces the old file there.
Sources: the repo's CLAUDE.md, docs/agents/*.md. Check the paths under "Copy from here" when the repo changes.
-->
# MBHAnalytics backend - agent instructions

ASP.NET Core 8 Minimal API for contract financial analytics. Clean Architecture + CQRS (MediatR 14), EF Core 8 +
PostgreSQL, FluentValidation 12, ErrorOr 2, Serilog, ASP.NET Core Identity + JWT.
This repo is the API. `Client/` is the old Blazor UI: it is a read-only reference for existing endpoints and DTOs and is never extended.

## Build and test
Same commands as `commands` of this component in `repos.yml`.

- Build: `dotnet build MBHAnalytics.sln`
- Test: `dotnet test MBHAnalytics.sln`
- Lint: `dotnet format --verify-no-changes`

Tests are in `Server/MBHAnalytics.Application.Tests`.

## Folder map
- `Server/MBHAnalytics.API/`: entry point, no controllers.
- `Server/MBHAnalytics.Application/`: CQRS features (`Features/{Domain}/{ActionName}.cs`), mappings (`Mappings/`), errors (`Common/Errors/`), route groups (`Extensions/ApiGroupExtension.cs`).
- `Server/MBHAnalytics.Domain/`: entities only, no behaviour.
- `Server/MBHAnalytics.Infrastructure/`: `ApplicationDbContext`, entity configurations (`Persistence/Configurations/`), services, `DependencyInjection.cs`.
- `Shared/MBHAnalytics.Shared/`: DTOs, `PaginationResponse<T>`. `Shared/MBHAnalytics.Security/`: JWT, claims, permissions.
- `docs/sql/`: schema scripts (see Database).

## Conventions
- One use case = one file `Features/{Domain}/{ActionName}.cs` with one outer class and nested `Endpoint`, `Command` or `Query`, `Validator`, `Handler`. Names: `Create{Entity}`, `GetList{Entities}`, `Update{Entity}`, `Delete{Entity}`, `Remove{Entity}`.
- A request, DTO or helper used by one use case is nested in its file. Only types shared by several use cases get their own file, grouped per module.
- Endpoints are found through `IEndpoint`; never register them by hand. Map routes through a group helper in `Application/Extensions/ApiGroupExtension.cs`, never on `app` directly. A new domain gets a new helper there.
- `[LogActivity(...)]` on every mutating command and on queries that belong in the audit trail.
- Validation runs in the MediatR `ValidationBehavior`; never call `ValidateAsync`. A validator targets the base upsert DTO, not `Command`.
- Handlers implement `IRequestHandler` or derive from `BaseHandler<TQuery, TResult>`. They take `IApplicationDbContextFactory` (never `ApplicationDbContext`) and create the context per call with `await using`. No repository layer: use `DbSet<T>` directly.
- Results are `ErrorOr<T>`. A query that pages returns `PaginationResponse<T>` directly, not wrapped in `ErrorOr<T>`. Never throw for a domain error: return an `Error`.
- Errors live in `Application/Common/Errors/ApplicationErrors.{Domain}.cs` (`static partial class ApplicationErrors`).
- Entity to DTO mapping: static extension methods in `Application/Mappings/` (for example `.ToContractDTO()`).
- New infrastructure services are registered in `Infrastructure/DependencyInjection.cs`. Strategies use `AddKeyedScoped<TInterface, TImpl>(key)` and `GetKeyedService<T>(key)`.
- Entities: no behaviour or validation. Mutable ones extend `BaseAuditableEntity`, insert-only ones `BaseCreateAuditableEntity`. Audit fields are stamped by `AuditSaveChangesInterceptor`; never set them by hand.
- Entity configuration: Fluent API only (no data annotations), snake_case columns with `HasColumnName`, extend `BaseAuditableEntityConfiguration<T>` and call `base.Configure(builder)`.
- Always put braces on `if`, `else if` and `else` bodies, also for one statement.
- Auth: JWT Bearer, policies from `AppClaims.Permission`. Use `ICurrentUserAccessor.GetUserEmpNo()` in handlers. Detail: `docs/agents/auth.md`.
- Tests: one test class per handler, in the folder of its domain under `Server/MBHAnalytics.Application.Tests`.
- Change style: do the smallest change that meets the plan. No extra features, no abstraction for one use, no refactor or reformat of code you were not asked to touch. Remove only what your own change made unused.

## Database: no EF Core migrations
Never create or edit anything in `Persistence/Migrations/`. A schema change is a plain idempotent SQL script in `docs/sql/` with the next number (`CREATE TABLE IF NOT EXISTS`, `ADD COLUMN IF NOT EXISTS`, `ON CONFLICT DO NOTHING`). Then mirror it by hand in C#:
1. the entity in `Domain/Entities/`,
2. its `IEntityTypeConfiguration<T>` in `Infrastructure/Persistence/Configurations/`,
3. the `DbSet<T>` in `ApplicationDbContext.cs` and `IApplicationDbContext.cs`.

Detail: `docs/agents/database.md`.

## Never edit
- `Server/MBHAnalytics.Infrastructure/Persistence/Migrations/`
- `Client/`
- `jenkins/`, `.github/`, `runs/`
- `docs/agents/pipeline.md`
- `appsettings*.json`

## Copy from here
- Create: `Server/MBHAnalytics.Application/Features/Contracts/CreateContract.cs`
- Paged list: `Server/MBHAnalytics.Application/Features/Contracts/GetListContracts.cs`
- Update: `Server/MBHAnalytics.Application/Features/Contracts/UpdateContract.cs`
- Mapping: `Server/MBHAnalytics.Application/Mappings/ContractMappings.cs`
- Errors: `Server/MBHAnalytics.Application/Common/Errors/ApplicationErrors.Contract.cs`
- Entity configuration: `Server/MBHAnalytics.Infrastructure/Persistence/Configurations/ContractConfiguration.cs`
- Handler test: `Server/MBHAnalytics.Application.Tests/ChargebackReport/ChargebackReportHandlerTests.cs`

## Open points
Nobody answers you while you work. When the plan is unclear or a rule here does not fit, do not guess and do not stop: do the part that is clear and write the question under "Open points" in PR_BODY.md.

## Review
Files a review can skip. Also in `review.skip_paths` in `repos.yml`.
- `docs/sql/reliability_*.sql`
- `**/*.md`
