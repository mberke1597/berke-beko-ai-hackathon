#!/usr/bin/env bash
# Stops what scripts/demo.sh started (backend, frontend, compose infrastructure).
# Usage: scripts/demo-stop.sh [--volumes]   (--volumes also deletes the database and monitoring volumes)
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
STATE_DIR="${TMPDIR:-/tmp}/beko-demo"

stop_pid() { # pid
  if command -v taskkill >/dev/null 2>&1; then
    taskkill //PID "$1" //T //F >/dev/null 2>&1 || true   # Git Bash: native Windows process tree
  else
    pkill -TERM -P "$1" 2>/dev/null || true
    kill "$1" 2>/dev/null || true
  fi
}

for name in frontend backend; do
  f="$STATE_DIR/$name.pid"
  if [ -f "$f" ]; then
    echo "[demo] stopping $name (pid $(cat "$f"))"
    stop_pid "$(cat "$f")"
    rm -f "$f"
  fi
done

# Anything still listening on the app ports (the PID file may hold a wrapper pid only).
for port in 5173 5080; do
  if command -v lsof >/dev/null 2>&1; then
    for pid in $(lsof -tiTCP:"$port" -sTCP:LISTEN 2>/dev/null); do
      echo "[demo] stopping pid $pid on :$port"; kill "$pid" 2>/dev/null || true
    done
  elif command -v netstat >/dev/null 2>&1 && command -v taskkill >/dev/null 2>&1; then
    for pid in $(netstat -ano | grep -E "[:.]$port .*LISTENING" | awk '{print $NF}' | sort -u); do
      echo "[demo] stopping pid $pid on :$port"; taskkill //PID "$pid" //T //F >/dev/null 2>&1 || true
    done
  fi
done

if command -v docker >/dev/null 2>&1; then
  if [ "${1:-}" = "--volumes" ]; then docker compose down -v; else docker compose down; fi
fi
echo "[demo] stopped."
