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

## Çeliknaz slice 1 — mock HomeWhiz, home, appliances

Design: `Planning/Celiknaz.md` §6, UI: `Planning/Celiknaz-UI.md`. Until auth exists (Phase 4), "me" means the seeded demo home and these endpoints need no token.

### Configuration

| Variable | Options / meaning | Default |
|---|---|---|
| `HomeWhiz__BaseUrl` | `HomeWhizOptions.BaseUrl`, base URL `IHomeWhizClient` calls | `http://localhost:5080/api/mock/homewhiz` (local), `http://localhost:8080/api/mock/homewhiz` (container) |
| `Simulation__TimeScale` | `SimulationOptions.TimeScale`, simulated seconds per real second | `1` (demo: `60`) |
| `Simulation__TickSeconds` | `SimulationOptions.TickSeconds`, real seconds between simulator ticks | `5` |
| `Simulation__OutdoorTempC` | `SimulationOptions.OutdoorTempC`, fixed outdoor temperature until the weather mock exists | `31` |

### Seeded demo home (stable ids)

- Home `home-1` "Evim", city İstanbul.
- Rooms: `salon` Salon, `yatak-odasi` Yatak Odası, `cocuk-odasi` Çocuk Odası, `mutfak` Mutfak, `camasir-odasi` Çamaşır Odası.
- Members: `ayse` Ayşe (owner, default), `mehmet` Mehmet (member), `zeynep` Zeynep (member).
- Appliances: `ac-salon`, `ac-yatak`, `ac-cocuk` (air_conditioner), `dishwasher-1` and `oven-1` and `fridge-1` (mutfak), `washer-1` (camasir-odasi). Initial `ac-salon`: on, cool, setpoint 25, room 24.8 °C, humidity 55 %.

### Appliance shape (shared by mock HomeWhiz and app API)

```json
{
  "id": "ac-salon",
  "homeId": "home-1",
  "roomId": "salon",
  "type": "air_conditioner",
  "displayName": "Salon Kliması",
  "model": "Beko BK-12 (demo)",
  "capabilities": {
    "commands": ["power", "setpoint", "mode", "fan"],
    "modes": ["cool", "heat", "dry", "fan", "auto", "eco"],
    "fanLevels": ["auto", "low", "medium", "high"],
    "setpoint": { "min": 16, "max": 30, "step": 0.5 },
    "programs": []
  },
  "state": {
    "power": true,
    "status": "running",
    "powerW": 820,
    "updatedAtUtc": "2026-11-03T10:00:00Z",
    "ac": { "setpoint": 25, "mode": "cool", "fan": "auto", "roomTempC": 24.8, "humidityPct": 55 },
    "program": null,
    "oven": null,
    "fridge": null
  }
}
```

- `type`: `air_conditioner` | `washing_machine` | `dishwasher` | `oven` | `refrigerator`.
- `state.status`: `off` | `idle` | `running` | `paused` | `finished` | `error`.
- `state.program` (washer, dishwasher): `{ "program": "eco50", "temperatureC": 50, "remainingMin": 42, "progressPct": 60, "startAtUtc": null }`.
- `state.oven`: `{ "mode": "conventional|fan|grill", "targetTempC": 180, "currentTempC": 120, "timerMin": 30 }`.
- `state.fridge`: `{ "fridgeTempC": 4, "freezerTempC": -18, "doorOpen": false }`.
- Exactly the sub-object for the appliance's type is non-null. Washer/dishwasher always have `state.program`: when idle it holds the selected program at full duration with `progressPct` 0, and `status` tells whether it runs.
- Oven `timerMin` is `0` when no timer is set.
- Oven target temperature uses `capabilities.setpoint` (`{ "min": 50, "max": 250, "step": 5 }`); oven `capabilities.modes` are `conventional`, `fan`, `grill`.
- **Simulated time**: `state.updatedAtUtc`, telemetry `tsUtc` and `DeviceEvent.atUtc` are simulator time (starts at real time, runs `TimeScale`× faster). Mongo `device_events.atUtc` is real time. Clients must not compare simulator timestamps with the browser clock.
- Rounding of visible state: 0.1 °C, 1 % humidity, 1 % progress, whole minutes (remaining, oven timer), 10 W power.

### Commands

`{ "type": "...", "value": ... }`

| `type` | `value` | Applies to |
|---|---|---|
| `power` | `true` / `false` | all except refrigerator |
| `setpoint` | number within `capabilities.setpoint` | air_conditioner |
| `mode` | one of `capabilities.modes` | air_conditioner, oven |
| `fan` | one of `capabilities.fanLevels` | air_conditioner |
| `start_program` | `{ "program": "eco50", "temperatureC": 50, "startAtUtc": null }` | washing_machine, dishwasher |
| `pause` / `resume` / `stop` | `null` | washing_machine, dishwasher, oven |
| `set_oven` | `{ "mode": "fan", "targetTempC": 180, "timerMin": 30 }` | oven |

Unsupported command or out-of-range value → `422` problem+json with a Turkish `title` and a stable `code`: `command_not_supported` | `invalid_value` | `value_out_of_range` | `invalid_state` (e.g. pause when not running). Clients map messages on `code`.

### Mock HomeWhiz API (simulates the external system; state lives in memory and resets on restart)

| Method + path | Response |
|---|---|
| `GET /api/mock/homewhiz/homes` | `[{ "id", "name", "city" }]` |
| `GET /api/mock/homewhiz/homes/{homeId}` | `{ "id", "name", "city", "location": { "lat", "lng" }, "rooms": [{ "id", "name" }], "members": [{ "id", "displayName", "role" }] }` |
| `GET /api/mock/homewhiz/homes/{homeId}/rooms` | `[{ "id", "name", "applianceIds": [] }]` |
| `GET /api/mock/homewhiz/homes/{homeId}/appliances` | `[Appliance]` |
| `GET /api/mock/homewhiz/appliances/{id}` | `Appliance` |
| `GET /api/mock/homewhiz/appliances/{id}/telemetry?fromUtc&toUtc` | `[{ "tsUtc", "roomTempC", "humidityPct", "powerW" }]` (AC: room values; others: `powerW` only) |
| `POST /api/mock/homewhiz/appliances/{id}/commands` | `202 { "commandId", "accepted": true, "appliance": Appliance }` or `422` |

### App API (what the frontend calls; reads through `IHomeWhizClient`)

| Method + path | Request / response |
|---|---|
| `GET /api/homes/me` | `{ "home": { "id", "name", "city" }, "rooms": [{ "id", "name" }], "members": [{ "id", "displayName", "role", "isDefault" }], "appliances": [Appliance] }` |
| `GET /api/devices/{id}` | `Appliance` |
| `POST /api/devices/{id}/commands` | `{ "type", "value", "source": "app\|remote\|voice\|ai\|schedule", "memberId": "ayse" }` → `202 { "commandId", "accepted", "appliance" }` or `422` |
| `GET /api/devices/{id}/telemetry?fromUtc&toUtc` | same as mock telemetry; without a range it returns the last 24 simulated hours (clients should call it without a range) |

`source` outside the allowed values → `400`; missing `memberId` defaults to `ayse`; unknown device → `404`.

Every accepted command writes a Mongo `device_events` document: `{ homeId, deviceId, source, memberId, command, before, after, atUtc, trace_id, span_id }`.

### SignalR `DeviceHub` — `/hubs/devices`

- Client → server: `JoinHome(homeId)`.
- Server → client (group `home:{homeId}`):
  - `DeviceStateChanged` `{ "appliance": Appliance }`: after every command and whenever the simulator changes visible state (rounded: 0.1 °C, 1 % humidity, 1 min remaining).
  - `DeviceEvent` `{ "applianceId", "event": "finished|preheated|timer_done|error", "atUtc" }`.

## Later phases

Phase 2+ sections (auth, agents, chat, runs, workflow, dashboard, SignalR `RunHub` events) are added here before those phases start.
