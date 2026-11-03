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
- Public URL: `https://<label>.<region>.cloudapp.azure.com` (Azure public IP DNS label; fallback `https://<VM-IP>.sslip.io`). TLS by cert-manager + Let's Encrypt, Traefik ingress built into k3s.

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

## Phase 2 — agent runtime, chat, runs (Çeliknaz first agent)

Design: `Planning/Proposal.md` §1 and §3, `docs/diagrams/03-agent-run-sequence.md`. No auth yet (Phase 4): the caller is household member `memberId` (default `ayse`), user id `demo-user`.

### Seeded agent
- Agent id `celiknaz`, name "Çeliknaz", model `Anthropic__DefaultModel`, Turkish instructions from `Planning/Celiknaz.md` §7 (short spoken answers, state what changed and its cost effect, ask before disruptive actions, allowed topics: the home's Beko products, comfort, energy, household tasks).
- Workflow v1 (Postgres, versioned): `trigger.user_message → agent.llm → respond`. Guard steps arrive in Phase 4; the engine must already support inserting them.
- Tools in this phase: `get_home_status` (home, members, all appliances with state, outdoor temp from `Simulation__OutdoorTempC`) and `control_device` (`{ applianceId, type, value }`, same command rules and 422 codes as slice 1, sent with `source: "ai"`). Both go through `IHomeWhizClient`.

### REST

| Method + path | Request / response |
|---|---|
| `GET /api/agents` | `[{ "id", "name", "description", "model" }]` |
| `GET /api/agents/{id}` | `{ "id", "name", "description", "model", "instructions", "tools": [{ "name", "description" }], "subagents": [], "workflowVersion" }` |
| `POST /api/conversations` | `{ "agentId": "celiknaz", "memberId": "ayse" }` → `201 { "conversationId", "agentId", "createdAtUtc" }` |
| `GET /api/conversations/{id}/messages` | `[{ "messageId", "role": "user\|assistant", "text", "runId", "createdAtUtc", "actions": [Action] }]` |
| `POST /api/conversations/{id}/messages` | `{ "text": "Ev soğudu, biraz ısıt", "channel": "voice\|text" }` → `202 { "runId", "traceId" }`; the run continues in the background and streams over `RunHub` |
| `GET /api/runs/{runId}` | `{ "runId", "conversationId", "agentId", "workflowVersion", "status": "running\|completed\|failed", "traceId", "startedAtUtc", "durationMs", "totals": Totals, "steps": [Step] }` |

- `Action`: `{ "applianceId", "displayName", "command", "value", "accepted": true }`, one per `control_device` call (`accepted: false` when the device rejected it).
- `Totals`: `{ "inputTokens", "outputTokens", "cacheReadTokens", "cacheWriteTokens", "costUsd", "durationMs" }`.
- `Step`: `{ "stepId", "parentStepId", "type", "name", "status": "pending\|running\|completed\|failed", "startedAtUtc", "durationMs", "tokens": { "input", "output" } | null, "costUsd", "summary", "spanId" }`. `type` is one of the workflow step types plus `llm.call` and `tool.call` children under `agent.llm`. `summary` is a short human-readable line (tool name + key args, or model + stop reason).
- Errors: problem+json. LLM failure → run `failed`, a `StepFailed` event, and then **always** a `RunCompleted` whose `text` is the Turkish fallback "Şu anda yanıt veremiyorum, lütfen tekrar deneyin." (stored as the assistant message). Every run ends with exactly one `RunCompleted`.

### SignalR `RunHub` — `/hubs/runs`
- Client → server: `JoinConversation(conversationId)`.
- Server → client (group `conversation:{conversationId}`):
  - `RunStarted` `{ runId, conversationId, agentId, traceId }`
  - `StepStarted` `{ runId, step: Step }`
  - `StepCompleted` `{ runId, step: Step }`
  - `StepFailed` `{ runId, step: Step, error }`
  - `TextDelta` `{ runId, delta }` (assistant text as it streams)
  - `RunCompleted` `{ runId, text, actions: [Action], totals: Totals }`

### Recording (every step, through the shared step wrapper)
OTel span (`invoke_agent celiknaz`, `chat {model}`, `execute_tool {tool}`, GenAI attributes), Mongo documents (`conversations`, `messages`, `runs`, `run_steps`, `llm_calls`, `tool_calls`, all with `trace_id`/`span_id`), the SignalR events above, metrics (`gen_ai_client_token_usage`, `gen_ai_cost_usd_total{agent,model}`, `agent_run_duration_seconds`, `tool_calls_total{tool,status}`). Cost from `PricingOptions`; daily rollup to Postgres `cost_daily`.

## Çeliknaz slice 2 — comfort, settings, presence, tasks, learning, notifications, dashboard

Design: `Planning/Celiknaz.md` §8–§14, `docs/diagrams/05,06,10,11`. Local demo scope: no auth (caller is `ayse` unless `memberId` is given), all state in memory + Mongo/Postgres as each module needs. Enums are lowercase snake_case strings.

### Settings and context

| Method + path | Body / response |
|---|---|
| `GET /api/me/settings?memberId=` | `{ "memberId", "priorityMode": "saving\|balanced\|comfort", "notificationLevel": "quiet\|balanced\|detailed", "quietHours": { "from": "23:00", "to": "08:00" }, "clothing": "light\|normal\|heavy", "locationSharing": true, "sensitivityOffset": 0.0, "estimatedMonthlyTl": { "saving": 380, "balanced": 460, "comfort": 590 } }` (default priority `saving`, notifications `balanced`, clothing `normal`) |
| `PUT /api/me/settings?memberId=` | same writable fields (`priorityMode`, `notificationLevel`, `quietHours`, `clothing`, `locationSharing`); `sensitivityOffset` and estimates are read-only |
| `GET /api/homes/me/context` | `{ "outdoorTempC", "outdoorHumidityPct", "season": "winter\|spring\|summer\|autumn", "simulatedNowUtc", "priorityMode", "members": [{ "memberId", "band": "home\|near\|away" }] }` |

`priorityMode` of the home context is the default member's. Config in `EnergyOptions`: `TariffTlPerKwh`, `TimeOfUseBands` (day/peak/night with TL per kWh), `RatedCoolingKw`, `RatedHeatingKw`, `SavingPerDegreePercent`, `DryModeFactor`, `FanModeFactor`, program energy profiles. `InfraCostOptions`: `MonthlyServerCostTl`.

### Comfort

`GET /api/devices/{id}/comfort?memberIds=ayse,mehmet` (default: members currently `home`) →
```json
{
  "applianceId": "ac-salon", "members": ["ayse"],
  "roomTempC": 24.8, "humidityPct": 55, "pmv": -0.4, "feeling": "slightly_cool",
  "inBand": true, "bandPmv": 1.0,
  "recommendation": { "action": "none|window|fan|dry|setpoint", "setpoint": 25.5, "mode": "cool", "fan": "auto", "reason": "Türkçe açıklama", "costPerHourTl": 1.2, "alternatives": [{ "action": "...", "setpoint": 0, "costPerHourTl": 0 }] },
  "conflict": { "present": false, "compromiseSetpoint": null, "perMember": [{ "memberId": "ayse", "pmvAtCompromise": 0.2 }] }
}
```
Engine: ISO 7730 PMV from room temp, humidity, fan→air speed, clothing (clo from season default or the member's `clothing`), activity (met by hour of day) plus the member's learned `sensitivityOffset`; band from the priority mode (saving ±1.0, balanced ±0.7, comfort ±0.5); multiple members → compromise by minimising the worst |PMV|; actions tried cheapest first: none/window (mild outdoor), fan, dry (humid), smallest setpoint step into the band. Cost from `EnergyOptions`.

`POST /api/me/comfort-feedback?memberId=` `{ "applianceId", "sensation": -1|0|1, "clothing": "light|normal|heavy"|null, "commandId": "..."|null }` → `204`. Updates the member's `sensitivityOffset` and context bias.

SignalR `DeviceHub` additions: `ComfortCheckRequested` `{ "applianceId", "memberId", "commandId" }` (about 20 simulated minutes after an AI-sourced command, only if notification level is not `quiet`), `NotificationCreated` `{ "notification": Notification }`.

### Energy

`GET /api/homes/me/energy` → `{ "todayKwh", "todayTl", "weekKwh", "weekTl", "savedWeekKwh", "savedWeekTl", "perDevice": [{ "applianceId", "todayKwh", "todayTl" }] }`. "Saved" is versus a baseline of the same hours at the member's usual setpoints without Çeliknaz decisions (simple estimate from `EnergyOptions`, documented in code).
`GET /api/homes/me/cheapest-time?applianceId=&programId=&deadlineUtc=` → `{ "startAtUtc", "costTl", "nowCostTl", "savingTl" }`.

### Presence and mock location

| Method + path | Response |
|---|---|
| `GET /api/mock/location?memberId=ayse` | `{ "memberId", "lat", "lng", "timestampUtc", "route": "at_home\|commute_out\|commute_back" }` (scripted position along a route; moves at `TimeScale`) |
| `PUT /api/mock/location/{memberId}/route` | `{ "route": "at_home\|commute_out\|commute_back" }` → `204` |
| `GET /api/homes/me/presence` | `[{ "memberId", "band", "distanceM", "etaMin": number\|null, "updatedAtUtc" }]` |
| `POST /api/me/presence?memberId=` | `{ "state": "leaving\|arriving" }` (spoken/tapped) → `204` |

`PresenceService` polls the mock location, computes distance to the home's location with a geofence (home < 300 m, near < 2 km, hysteresis), stores only bands. Automatic actions (with `source: "ai"`, a Notification each): nobody `home` → by priority mode of the default member: `saving` AC off, `balanced` eco setback (cool 28 / heat 18), `comfort` mild setback; someone approaching with eta below pre-condition time (or learned return time near) → `balanced`/`comfort` pre-condition, `saving` waits for arrival.

### Household tasks

`HouseholdTask`: `{ "id", "title", "origin": "device_event\|routine\|manual\|voice", "applianceId": null, "assignedMemberId", "status": "open\|done\|skipped", "effortPoints", "createdAtUtc", "dueAtUtc": null, "reason": "Adil dağıtım: son 7 günde en az iş yapan" }`.
`TaskRule`: `{ "id", "trigger": "washing_machine.finished", "template": "Çamaşırları as", "strategy": "fair_share\|round_robin\|fixed\|ask", "fixedMemberId": null, "effortPoints": 2, "enabled": true }`. Seeded rules: washer finished → "Çamaşırları as", dishwasher finished → "Bulaşık makinesini boşalt", oven timer_done → "Fırını kontrol et" (disabled).

| Method + path | Body / response |
|---|---|
| `GET /api/homes/me/tasks?status=open` | `[HouseholdTask]` |
| `POST /api/homes/me/tasks` | `{ "title", "assignedMemberId": null, "effortPoints": 1 }` → `201 HouseholdTask` (null assignee → distributed by `fair_share`) |
| `POST /api/tasks/{id}/done` / `skip` | `HouseholdTask` (done adds effort points to the assignee) |
| `POST /api/tasks/{id}/reassign` | `{ "memberId": null }` → `HouseholdTask` (null = redistribute) |
| `GET /api/homes/me/task-rules`, `PUT /api/homes/me/task-rules/{id}` | rules; PUT updates `strategy`, `fixedMemberId`, `template`, `effortPoints`, `enabled` |
| `GET /api/homes/me/task-stats` | `[{ "memberId", "pointsLast7Days", "doneLast7Days" }]` |

`fair_share`: among members whose presence is `home` (else all), lowest `pointsLast7Days`, ties by least recent task. A new task from a `DeviceEvent` is created by the backend itself; creation emits SignalR `TaskAssigned` `{ "task": HouseholdTask }` and a Notification.

### Learning

| Method + path | Response |
|---|---|
| `GET /api/me/profile?memberId=` | `{ "sensitivityOffset", "preferences": [{ "context": "weekday evening, hot", "setpoint": 24.5, "confidence": 0.8, "samples": 13, "text": "Türkçe özet" }], "routines": [Routine], "overrideRate": [{ "weekStartUtc", "rate" }], "goodFeedbackRate": [{ "weekStartUtc", "rate" }] }` |
| `POST /api/me/routines/{id}/accept` / `reject` | `Routine` (accept creates a schedule) |
| `DELETE /api/me/learning?memberId=` | `204` resets profile, routines and offset |
| `POST /api/demo/seed-history` | `{ "memberId": "ayse", "weeks": 5 }` → `{ "events": N }` generates synthetic `device_events`/feedback from personas (Development only) and runs the learner immediately |

`Routine`: `{ "id", "action": "power_on", "applianceId", "dayType": "weekday\|weekend", "time": "18:30", "windowMin": 20, "confidence": 0.85, "evidence": "Son 3 haftada 13 kez 18:30 civarı açtınız", "status": "detected\|suggested\|accepted\|rejected" }`.
`PatternLearner` (`BackgroundService`, plus on-demand after seeding): preferred setpoint per context (time-weighted median, 14-day half-life), routines (recurring within ±20 min, circular time), override detection (AI command followed within 30 simulated minutes by a `remote`/`app` command on the same device → delta, `bias += 0.3 × delta`), sensitivity from comfort feedback vs predicted PMV.

### Notifications

`Notification`: `{ "id", "memberId", "kind": "info\|suggestion\|comfort\|task\|alert", "level": "critical\|important\|info", "title", "text", "createdAtUtc", "read": false, "action": null | { "type": "accept_routine", "routineId": "..." } }`.
`GET /api/me/notifications?memberId=`, `POST /api/me/notifications/{id}/read`. Delivery honours the member's `notificationLevel` (quiet: critical only; balanced: critical + important; detailed: all), quiet hours (critical only), and a cap of 3 suggestions per day.

### Dashboard (Arçelik)

| Method + path | Response |
|---|---|
| `GET /api/dashboard/summary` | `{ "homes": 1, "activeMembers", "commandsToday": { "voice", "app", "remote", "ai", "schedule" }, "conversationsToday", "runsToday", "llm": { "costUsdToday", "costUsdMonth", "tokensToday", "avgCostUsdPerConversation", "avgLatencyMs" }, "energy": { "savedKwhWeek", "savedTlWeek" }, "learning": { "overrideRateTrend": [...], "goodFeedbackTrend": [...], "suggestionAcceptanceRate" }, "tools": [{ "tool", "calls", "errors" }], "server": { "monthlyCostTl", "costPerActiveUserTl" } }` |
| `GET /api/dashboard/projection?homes=100000` | `{ "homes", "llmCostUsdMonth", "serverCostTlMonth", "savedKwhMonth", "savedTlMonth", "assumptions": [...] }` (linear extrapolation of the demo home's per-home averages; assumptions listed in Turkish) |
| `GET /api/runs?limit=50` | `[{ "runId", "conversationId", "agentId", "status", "startedAtUtc", "durationMs", "costUsd", "traceId", "userText" }]` |

### Agent tools added in slice 2
`get_comfort_assessment`, `report_thermal_sensation`, `get_member_profile`, `set_presence`, `get_settings`, `update_settings`, `find_cheapest_time`, `estimate_energy`, `list_tasks`, `create_task`, `assign_task`, `propose_automation`. Each goes through the shared step wrapper like `control_device`.

## Later phases

Phase 2+ sections (auth, agents, chat, runs, workflow, dashboard, SignalR `RunHub` events) are added here before those phases start.
