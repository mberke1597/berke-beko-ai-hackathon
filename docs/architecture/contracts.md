# Contracts

Source of truth shared by backend, frontend and infra. Only the lead changes this file. Sections are added per phase.

## Services, ports and names

| Service | Container port | Local dev | k8s Service name |
|---|---|---|---|
| backend (ASP.NET Core) | 8080 | http://localhost:5080 (`dotnet run`) | `backend` |
| frontend (nginx serving Vite build) | 80 | http://localhost:5173 (Vite dev) | `frontend` |
| PostgreSQL 17 | 5432 | localhost:5432 | `postgres` |
| MongoDB 8 | 27017 | localhost:27017 | `mongodb` |
| OTel Collector (OTLP gRPC / HTTP) | 4317 / 4318 | localhost:4317 / 4318 | `otel-collector` |
| Prometheus | 9090 | localhost:9090 | `prometheus` |
| Tempo | 3200 (+4317 internal) | — | `tempo` |
| Loki | 3100 | — | `loki` |
| Grafana | 3000 | localhost:3000 | `grafana` |

- Kubernetes namespace: `beko`.
- Images: `ghcr.io/mberke1597/beko-backend:<git-sha>`, `ghcr.io/mberke1597/beko-frontend:<git-sha>` (plus `:latest` on `main`). Platform `linux/arm64`; CI and local Compose may also build `linux/amd64`.
- Public URL: `https://<VM-IP>.sslip.io` (TLS by cert-manager + Let's Encrypt, Traefik ingress built into k3s).

## Routing (same origin, no CORS needed in production)

| Path prefix | Goes to |
|---|---|
| `/api/` | backend |
| `/hubs/` | backend (SignalR, WebSocket) |
| `/health` | backend |
| `/grafana/` | Grafana (`serve_from_sub_path = true`, root URL `/grafana/`) |
| `/` | frontend |

- k3s: Traefik Ingress rules above.
- Compose: the frontend nginx proxies `/api/`, `/hubs/` (with WebSocket upgrade) and `/health` to `backend:8080`; Grafana is also exposed on :3000.
- Vite dev: `vite.config.ts` proxies `/api`, `/hubs` (ws) and `/health` to `http://localhost:5080`.

## Backend configuration (environment variables)

ASP.NET Core double-underscore binding. Options classes are validated on start.

| Variable | Options / meaning | Example |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | environment | `Development` / `Production` |
| `ASPNETCORE_HTTP_PORTS` | listen port in containers | `8080` |
| `ConnectionStrings__Postgres` | Npgsql connection string | `Host=postgres;Database=beko;Username=beko;Password=...` |
| `Mongo__ConnectionString` | `MongoOptions.ConnectionString` | `mongodb://beko:...@mongodb:27017` |
| `Mongo__Database` | `MongoOptions.Database` | `beko` |
| `Anthropic__ApiKey` | `AnthropicOptions.ApiKey` (secret) | — |
| `Anthropic__DefaultModel` | `AnthropicOptions.DefaultModel` | `claude-sonnet-5-5` |
| `Anthropic__FastModel` | `AnthropicOptions.FastModel` | `claude-haiku-5-5` |
| `Anthropic__MaxTokens` | `AnthropicOptions.MaxTokens` | `4096` |
| `Jwt__SigningKey` | `JwtOptions.SigningKey` (secret, ≥ 32 bytes) | — |
| `Jwt__Issuer` / `Jwt__Audience` | `JwtOptions` | `beko-agents` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OTLP endpoint (standard OTel env var) | `http://otel-collector:4317` |
| `OTEL_SERVICE_NAME` | service name | `beko-backend` |
| `App__Version` / `App__Commit` | shown by `/api/version` | `0.1.0` / git sha |
| `App__GrafanaBaseUrl` | used to build trace links | `/grafana` |

Pricing lives in `appsettings.json` under `Pricing:Models:{model}` with `InputPerMTok`, `OutputPerMTok`, `CacheReadPerMTok`, `CacheWritePerMTok` (USD).

Postgres container env: `POSTGRES_DB=beko`, `POSTGRES_USER=beko`, `POSTGRES_PASSWORD`. Mongo container env: `MONGO_INITDB_ROOT_USERNAME=beko`, `MONGO_INITDB_ROOT_PASSWORD`.

## Phase 1 endpoints

### `GET /health`
Liveness. `200` with body `Healthy`. No auth.

### `GET /health/ready`
Readiness: checks PostgreSQL and MongoDB. `200 Healthy` or `503 Unhealthy`. No auth. Used by k8s readiness probe.

### `GET /api/version`
No auth.
```json
{ "name": "Beko Agent Platform", "version": "0.1.0", "commit": "abc1234", "environment": "Production", "serverTimeUtc": "2026-11-03T10:00:00Z" }
```

### Errors (all endpoints)
RFC 9457 `application/problem+json` (`type`, `title`, `status`, `detail`, `traceId`). The frontend shows a Turkish message based on `status`.

## Later phases

Phase 2+ sections (auth, agents, chat, runs, workflow, dashboard, SignalR `RunHub` events) are added here before those phases start.
