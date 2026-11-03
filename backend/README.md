# Backend

.NET 10 solution `Beko.Agents.slnx` (Domain, Application, Infrastructure, Api and tests). Rules: `AGENTS.md` at the repo root, API contract: `docs/architecture/contracts.md`.

## Database migrations (PostgreSQL, EF Core)

Migrations live in `src/Beko.Agents.Infrastructure/Persistence/Postgres/Migrations`. The agent configuration seed (agent `celiknaz`, its tools, tone presets and workflow v1) is part of the migration.

- **Development**: the API applies pending migrations on startup. Turn it off with `Database__MigrateOnStartup=false`.
- **Any other environment** (Production, k3s): migrations are not applied by the app. Apply them before starting a new version:

  ```bash
  cd backend
  dotnet tool restore
  dotnet ef database update \
    --project src/Beko.Agents.Infrastructure \
    --startup-project src/Beko.Agents.Api \
    --connection "Host=postgres;Database=beko;Username=beko;Password=..."
  ```

  Or generate an idempotent SQL script and run it with `psql`:

  ```bash
  dotnet ef migrations script --idempotent \
    --project src/Beko.Agents.Infrastructure --startup-project src/Beko.Agents.Api -o migrate.sql
  ```

- **New migration** after changing entities or configurations:

  ```bash
  dotnet ef migrations add <Name> \
    --project src/Beko.Agents.Infrastructure --startup-project src/Beko.Agents.Api \
    --output-dir Persistence/Postgres/Migrations
  ```

`dotnet-ef` is pinned in `.config/dotnet-tools.json`.

## Tests

`dotnet test --solution Beko.Agents.slnx`. The LLM is always a fake (`Beko.Agents.TestSupport`); no test calls the Anthropic API. The tests in `Beko.Agents.Infrastructure.Tests/Persistence` start PostgreSQL and MongoDB containers with Testcontainers, so Docker must be running.
