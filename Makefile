# Beko Agent Platform - developer commands (GNU make). Run from the repo root.
# On Windows without make, run the command lines under each target by hand.

.PHONY: help up infra down demo demo-stop logs backend-run frontend-dev test backend-test frontend-test \
        lint backend-lint frontend-lint build backend-build frontend-build images k8s-render

PUBLIC_HOST ?= beko.example.test
IMAGE_TAG   ?= latest

help:
	@echo "demo | demo-stop | infra | up | down | logs | backend-run | frontend-dev | test | lint | build | images | k8s-render"

## One-command local demo: infra in Docker, backend + frontend from source (see docs/development/local-demo.md)
ifeq ($(OS),Windows_NT)
demo:
	powershell -NoProfile -ExecutionPolicy Bypass -File scripts/demo.ps1
demo-stop:
	powershell -NoProfile -ExecutionPolicy Bypass -File scripts/demo-stop.ps1
else
demo:
	bash scripts/demo.sh
demo-stop:
	bash scripts/demo-stop.sh
endif

## Local stack (Docker Compose). Needs .env (cp .env.example .env).
## infra = databases + observability only; up = everything incl. built backend and frontend images.
infra:
	@test -f .env || { echo "Missing .env: cp .env.example .env and fill in values"; exit 1; }
	docker compose up -d

up:
	@test -f .env || { echo "Missing .env: cp .env.example .env and fill in values"; exit 1; }
	docker compose --profile full up -d --build

down:
	docker compose --profile full down

logs:
	docker compose --profile full logs -f --tail=100

## Run apps locally. They need the database/OTLP settings from compose.yml: prefer `make demo`, which sets everything.
backend-run:
	cd backend && dotnet run --project src/Beko.Agents.Api --urls http://localhost:5080

frontend-dev:
	cd frontend && npm install && npm run dev

## Tests
test: backend-test frontend-test

backend-test:
	cd backend && dotnet test --solution Beko.Agents.slnx

frontend-test:
	cd frontend && npm ci && npm test

## Lint / format / typecheck
lint: backend-lint frontend-lint

backend-lint:
	cd backend && dotnet format Beko.Agents.slnx --verify-no-changes

frontend-lint:
	cd frontend && npm ci && npm run lint && npm run typecheck

## Build
build: backend-build frontend-build

backend-build:
	cd backend && dotnet build Beko.Agents.slnx -c Release

frontend-build:
	cd frontend && npm ci && npm run build

## Container images for the local platform
images:
	docker build -f deploy/docker/backend.Dockerfile -t ghcr.io/mberke1597/beko-backend:local .
	docker build -f deploy/docker/frontend.Dockerfile -t ghcr.io/mberke1597/beko-frontend:local .

## Print the rendered k8s manifests (what deploy.yml applies)
k8s-render:
	@kubectl kustomize --load-restrictor LoadRestrictionsNone deploy/kubernetes \
	  | PUBLIC_HOST=$(PUBLIC_HOST) IMAGE_TAG=$(IMAGE_TAG) envsubst '$${PUBLIC_HOST} $${IMAGE_TAG}'
