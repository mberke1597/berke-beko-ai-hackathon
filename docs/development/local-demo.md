# Local demo

Run the whole platform on your own machine with one command. Infrastructure (databases and observability) runs in Docker; the backend and the frontend run from source.

```powershell
# Windows PowerShell, from the repository root
scripts\demo.ps1          # start
scripts\demo-stop.ps1     # stop (add -Volumes to delete all local data)
```

```bash
# Linux, macOS, Git Bash
scripts/demo.sh           # start (options: --no-frontend, --time-scale N)
scripts/demo-stop.sh      # stop (add --volumes to delete all local data)
```

With GNU make installed: `make demo` and `make demo-stop` run the right script for your OS.

## Prerequisites

| Tool | Version | Check |
|---|---|---|
| Docker Desktop (or Docker Engine + Compose v2) | running | `docker info` |
| .NET SDK | 10 | `dotnet --version` |
| Node.js | 22 or newer | `node --version` |

## What the script does

1. Checks Docker, dotnet and node.
2. Creates `.env` from `.env.example` if it is missing (placeholders are fine for a local demo).
3. Picks the host ports of the databases: 5432 and 27017, or 15432 and 27018 when something on your machine already uses them (the script says which program holds the port).
4. Starts `docker compose up -d`: postgres, mongodb, otel-collector, prometheus, tempo, loki, grafana. Waits for the databases to be healthy.
5. Starts the backend in a minimized window: `dotnet run` in `Development`, `Simulation__TimeScale=60` (one real second is one simulated minute), OTLP export to the local collector.
6. Starts the frontend in a minimized window: `npm run dev` (installs dependencies the first time).
7. Waits for `http://localhost:5080/health/ready` and `http://localhost:5173/`, then prints the URLs.

Running it twice is safe: services that already answer are reused.

## URLs and ports

| What | URL |
|---|---|
| App (Çeliknaz) | http://localhost:5173/#/ev |
| Arçelik panel | http://localhost:5173/#/panel |
| Backend API | http://localhost:5080/api/version, health: `/health`, `/health/ready` |
| Grafana | http://localhost:3000/grafana/ (user `admin`, password `GRAFANA_ADMIN_PASSWORD` from `.env`, default `changeme`) |
| Prometheus | http://localhost:9090 |
| OTLP into the collector | `localhost:4317` (gRPC), `localhost:4318` (HTTP) |
| PostgreSQL / MongoDB | `127.0.0.1:5432` / `127.0.0.1:27017`, or the alternative ports printed by the script |

Tempo and Loki are internal; open them through Grafana (Explore, or the dashboards "Çeliknaz — Genel" and "LLM ve Maliyet" in the folder "Beko").

Logs of the backend and frontend: `%TEMP%\beko-demo\` (Windows) or `${TMPDIR:-/tmp}/beko-demo/` (bash).

## The Anthropic API key

The AI features (chat, voice commands) need a key. The script never prints it and does not require it:

- Preferred: put it in the git-ignored `backend/src/Beko.Agents.Api/appsettings.Local.json` (section `Anthropic`, property `ApiKey`). The backend loads this file by itself. The script only checks that the file exists and no longer contains the placeholder `PUT-YOUR-KEY-HERE`.
- Alternatives: set the environment variable `Anthropic__ApiKey` before running the script, or put `ANTHROPIC_API_KEY=...` into `.env` (git-ignored; also used by `docker compose --profile full`).

Without a key the demo still runs (devices, simulator, dashboards); only the AI parts fail, and the script prints a warning.

## Other ways to run

| Goal | Command |
|---|---|
| Infrastructure only, run backend and frontend yourself | `docker compose up -d` (or `make infra`), then `dotnet run --project backend/src/Beko.Agents.Api --urls http://localhost:5080` and `npm run dev` in `frontend/` |
| Everything in containers (built images) | `docker compose --profile full up -d --build` (or `make up`): app on http://localhost:8081, API on :5080 |
| Frontend without a backend (mock mode) | `cd frontend && npm run dev:mock` (http://localhost:5173, no Docker or backend needed) |

When you run the backend yourself it needs the same settings the script sets: `ConnectionStrings__Postgres`, `Mongo__ConnectionString`, `Jwt__SigningKey`, and optionally `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317`, `OTEL_SERVICE_NAME=beko-backend`, `Simulation__TimeScale=60`. The values for local use are in `compose.yml` and `.env.example`.

## Checking telemetry

After using the app for a minute: Grafana → Dashboards → Beko → "Çeliknaz — Genel" shows HTTP latency, device commands by source and type, and logs. "LLM ve Maliyet" fills once the agent runtime emits `gen_ai_cost_usd_total`, `agent_run_duration_seconds` and `tool_calls_total`. Metrics reach Prometheus about a minute after the first request.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `Docker daemon is not running` | Start Docker Desktop and wait until it says running. |
| `Port 5080 is held by ...` | Another program (or an old backend) uses 5080, and the frontend proxy needs it. Run `scripts\demo-stop.ps1`, or stop the program named in the message. |
| `Port 5432 is held by ...; using 15432` | Informational: a local PostgreSQL uses 5432. Docker publishes on the alternative port and the script gives the backend the right connection string. To pick ports yourself set `POSTGRES_HOST_PORT` / `MONGO_HOST_PORT` in `.env`. |
| Backend window closes or `/health/ready` never turns 200 | Open `%TEMP%\beko-demo\backend.log`. Typical causes: database password in `.env` changed after the first start (the databases keep the old one: `scripts\demo-stop.ps1 -Volumes`, then start again), or a build error. |
| Frontend shows a network error | The backend is not up yet or was stopped. Check `http://localhost:5080/api/version`. |
| `/health/ready` is 503 for the first seconds | The databases are still starting; the script waits for it. |
| AI chat does not answer | No key: see "The Anthropic API key". |
| Grafana dashboards are empty | Metrics need about a minute and real traffic; check the time range (last 1 hour). |
| PowerShell blocks the script | Run `powershell -ExecutionPolicy Bypass -File scripts\demo.ps1`. |
| Start over with clean data | `scripts\demo-stop.ps1 -Volumes` (or `scripts/demo-stop.sh --volumes`), then start again. |
