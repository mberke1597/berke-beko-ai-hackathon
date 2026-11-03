# Beko Agent Platform - developer commands (GNU make). Run from the repo root.
# On Windows without make, run the command lines under each target by hand.

.PHONY: help up down logs backend-run frontend-dev test backend-test frontend-test \
        lint backend-lint frontend-lint build backend-build frontend-build images k8s-render

PUBLIC_HOST ?= 127.0.0.1.sslip.io
IMAGE_TAG   ?= latest

help:
	@echo "up | down | logs | backend-run | frontend-dev | test | lint | build | images | k8s-render"

## Local stack (Docker Compose). Needs .env (cp .env.example .env).
up:
	@test -f .env || { echo "Missing .env: cp .env.example .env and fill in values"; exit 1; }
	docker compose up -d --build

down:
	docker compose down

logs:
	docker compose logs -f --tail=100

## Run apps locally (start postgres/mongodb first: docker compose up -d postgres mongodb)
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
