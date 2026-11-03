#!/usr/bin/env bash
# One-command local demo (bash: Linux, macOS, Git Bash on Windows).
# Infrastructure in Docker, backend (dotnet run) and frontend (npm run dev) from source in the background.
# The Anthropic key is read from the ANTHROPIC_API_KEY environment variable or from .env and is never printed.
# Stop with scripts/demo-stop.sh. Usage: scripts/demo.sh [--no-frontend] [--time-scale N]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
STATE_DIR="${TMPDIR:-/tmp}/beko-demo"
mkdir -p "$STATE_DIR"
NO_FRONTEND=0
TIME_SCALE=60
while [ $# -gt 0 ]; do
  case "$1" in
    --no-frontend) NO_FRONTEND=1 ;;
    --time-scale) shift; TIME_SCALE="$1" ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

info() { printf '\033[36m[demo] %s\033[0m\n' "$*"; }
warn() { printf '\033[33m[demo] %s\033[0m\n' "$*"; }
fail() { printf '\033[31m[demo] %s\033[0m\n' "$*" >&2; exit 1; }

need() { command -v "$1" >/dev/null 2>&1 || fail "'$1' not found. $2"; }

port_in_use() { (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null; }

port_owner() { # best-effort description of the listener on a port
  if command -v lsof >/dev/null 2>&1; then
    lsof -nP -iTCP:"$1" -sTCP:LISTEN 2>/dev/null | awk 'NR==2 {print $1 " (pid " $2 ")"}'
  elif command -v netstat >/dev/null 2>&1; then
    local pid
    pid="$(netstat -ano 2>/dev/null | grep -E "[:.]$1 .*LISTENING" | awk '{print $NF}' | head -1)"
    [ -n "$pid" ] && echo "pid $pid"
  fi
}

wait_http() { # url seconds what
  local deadline=$((SECONDS + $2))
  while [ $SECONDS -lt $deadline ]; do
    if curl -fsS -o /dev/null -m 3 "$1" 2>/dev/null; then return 0; fi
    sleep 2
  done
  fail "$3 did not become ready within $2 s ($1). Log: $STATE_DIR"
}

# 1. Prerequisites
need docker 'Install Docker and start it.'
need dotnet 'Install the .NET 10 SDK.'
need node 'Install Node.js 22 or newer.'
need npm 'Install Node.js 22 or newer.'
need curl 'Install curl.'
docker info >/dev/null 2>&1 || fail 'Docker daemon is not running.'
info "dotnet $(dotnet --version), node $(node --version), docker ok"

# 2. .env
if [ ! -f .env ]; then
  cp .env.example .env
  info 'Created .env from .env.example (placeholders; set ANTHROPIC_API_KEY there for the AI features).'
fi
# Read a setting: environment first, then .env (without sourcing it, so values are not interpreted).
setting() { # name default
  local v="${!1:-}"
  if [ -z "$v" ]; then
    v="$(grep -E "^[[:space:]]*$1=" .env | tail -1 | cut -d= -f2- | tr -d '\r' || true)"
  fi
  printf '%s' "${v:-${2:-}}"
}

# 3. Database host ports (a local Postgres/MongoDB may already use 5432/27017)
resolve_port() { # service container_port env_name preferred fallback
  local explicit published
  explicit="$(setting "$3" '')"
  if [ -n "$explicit" ]; then echo "$explicit"; return; fi
  published="$(docker compose port "$1" "$2" 2>/dev/null | sed -E 's/.*:([0-9]+)\s*$/\1/' || true)"
  if [ -n "$published" ]; then echo "$published"; return; fi
  if ! port_in_use "$4"; then echo "$4"; return; fi
  warn "Port $4 is held by $(port_owner "$4" || true); using $5 for $1." >&2
  echo "$5"
}
PG_PORT="$(resolve_port postgres 5432 POSTGRES_HOST_PORT 5432 15432)"
MONGO_PORT="$(resolve_port mongodb 27017 MONGO_HOST_PORT 27017 27018)"
export POSTGRES_HOST_PORT="$PG_PORT" MONGO_HOST_PORT="$MONGO_PORT"

# 4. Infrastructure
info 'Starting infrastructure (postgres, mongodb, otel-collector, prometheus, tempo, loki, grafana) ...'
docker compose up -d
deadline=$((SECONDS + 120))
for svc in postgres mongodb; do
  while true; do
    id="$(docker compose ps -q "$svc")"
    health=""
    [ -n "$id" ] && health="$(docker inspect --format '{{.State.Health.Status}}' "$id")"
    [ "$health" = "healthy" ] && break
    [ $SECONDS -gt $deadline ] && fail "$svc is not healthy (docker compose logs $svc)."
    sleep 2
  done
done
info "Databases healthy (postgres :$PG_PORT, mongodb :$MONGO_PORT)."

# 5. Backend
REUSE_BACKEND=0
if port_in_use 5080; then
  if curl -fsS -o /dev/null -m 3 http://localhost:5080/health 2>/dev/null; then
    warn 'Something already answers on :5080 (previous demo run?). Reusing it. Run scripts/demo-stop.sh first for a clean start.'
    REUSE_BACKEND=1
  else
    fail "Port 5080 is held by $(port_owner 5080 || true), which is not a Beko backend. The frontend proxy needs :5080: stop it (or run scripts/demo-stop.sh) and retry."
  fi
fi
if [ "$REUSE_BACKEND" = 0 ]; then
  # Key sources: Anthropic__ApiKey (environment), the git-ignored backend/src/Beko.Agents.Api/appsettings.Local.json
  # (loaded by the backend itself) or an optional ANTHROPIC_API_KEY in the environment/.env. Values are never printed.
  local_json="backend/src/Beko.Agents.Api/appsettings.Local.json"
  has_key=0
  if [ -f "$local_json" ]; then
    local_key="$(grep -oE '"ApiKey"[[:space:]]*:[[:space:]]*"[^"]+"' "$local_json" | head -1 | sed -E 's/.*:[[:space:]]*"([^"]*)"//' || true)"
    if [ -n "$local_key" ] && [ "$local_key" != "PUT-YOUR-KEY-HERE" ]; then has_key=1; fi
    unset local_key
  fi
  api_key="${Anthropic__ApiKey:-$(setting ANTHROPIC_API_KEY '')}"
  # Variables for the backend process only (not exported to this shell).
  be_env=(
    "ASPNETCORE_ENVIRONMENT=Development"
    "ConnectionStrings__Postgres=Host=127.0.0.1;Port=$PG_PORT;Database=beko;Username=beko;Password=$(setting POSTGRES_PASSWORD changeme)"
    "Mongo__ConnectionString=mongodb://beko:$(setting MONGO_PASSWORD changeme)@127.0.0.1:$MONGO_PORT/?authSource=admin"
    "Mongo__Database=beko"
    "Jwt__SigningKey=$(setting JWT_SIGNING_KEY dev-only-signing-key-at-least-32-bytes-long)"
    "OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317"
    "OTEL_SERVICE_NAME=beko-backend"
    "Simulation__TimeScale=$TIME_SCALE"
    "App__Commit=local-demo"
  )
  case "$api_key" in
    ''|changeme|'<'*) ;;
    *) be_env+=("Anthropic__ApiKey=$api_key"); has_key=1 ;;
  esac
  if [ "$has_key" = 1 ]; then
    info 'Anthropic key found (value not shown). AI features enabled.'
  else
    warn 'No Anthropic key (appsettings.Local.json is missing or still PUT-YOUR-KEY-HERE, Anthropic__ApiKey is not set): the app runs, but AI chat/voice will not work.'
  fi
  info 'Starting backend (dotnet run) in the background ...'
  (cd backend && nohup env "${be_env[@]}" dotnet run --project src/Beko.Agents.Api --urls http://localhost:5080 \
    >"$STATE_DIR/backend.log" 2>&1 & echo $! >"$STATE_DIR/backend.pid")
  unset api_key be_env
fi

# 6. Frontend
if [ "$NO_FRONTEND" = 0 ]; then
  if port_in_use 5173; then
    warn "Port 5173 is held by $(port_owner 5173 || true) (previous demo run or another Vite). Reusing it."
  else
    if [ ! -d frontend/node_modules ]; then
      info 'Installing frontend dependencies (npm ci) ...'
      (cd frontend && npm ci --no-audit --no-fund)
    fi
    info 'Starting frontend (npm run dev) in the background ...'
    (cd frontend && nohup npm run dev >"$STATE_DIR/frontend.log" 2>&1 & echo $! >"$STATE_DIR/frontend.pid")
  fi
fi

# 7. Wait
info 'Waiting for the backend (first start compiles, can take a minute) ...'
wait_http http://localhost:5080/health/ready 240 Backend
[ "$NO_FRONTEND" = 0 ] && wait_http http://localhost:5173/ 90 Frontend

cat <<EOF

Ready.
  App        http://localhost:5173/#/ev
  Panel      http://localhost:5173/#/panel
  API        http://localhost:5080/api/version
  Grafana    http://localhost:3000/grafana/   (admin / GRAFANA_ADMIN_PASSWORD from .env, default changeme)
  Prometheus http://localhost:9090

Logs: $STATE_DIR   Stop: scripts/demo-stop.sh   (or: make demo-stop)
EOF
