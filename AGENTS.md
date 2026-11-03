# AGENTS.md

Single source of rules for every developer and AI agent working in this repository. `CLAUDE.md` imports this file.

## What this is

**Beko Agent Platform**: a reusable, enterprise-grade AI agent platform for the Beko (Arçelik) AI Hackathon. Agents, subagents, tools and workflows are configured as data; every step of every run is traced, logged and costed. Read `Planning/Proposal.md` (architecture) and `Planning/Phases.md` (execution order) before starting work.

## Repository layout and ownership

| Path | Contents | Owner |
|---|---|---|
| `backend/` | .NET 10 solution `Beko.Agents` (Domain, Application, Infrastructure, Api + tests) | backend |
| `frontend/` | Vite + TypeScript app, no UI framework | frontend |
| `deploy/docker/`, `deploy/kubernetes/` | Dockerfiles (linux/arm64), k3s manifests | infra |
| `monitoring/` | OTel Collector, Prometheus, Tempo, Loki, Grafana provisioning | infra |
| `.github/workflows/`, `compose.yml`, `Makefile`, `.env.example` | CI/CD, local stack, commands | infra |
| `docs/`, `AGENTS.md`, `CLAUDE.md`, `Planning/` | documentation, contracts, plans | lead |

Never edit files owned by someone else. If you need a change there, say so in your report.

## Contracts

`docs/architecture/contracts.md` is the source of truth for REST endpoints, SignalR events, ports, service names, environment variables and image names. Backend, frontend and infra must all match it. Change it only through the lead.

## Fixed technology

- Backend: .NET 10, C#, ASP.NET Core Web API with **controllers**, Clean Architecture (`Domain` ← `Application` ← `Infrastructure`, `Api`). Options pattern with `ValidateOnStart`. Central package management (`Directory.Packages.props`).
- LLM: official `Anthropic` NuGet package only. Default models `claude-sonnet-5-5` (agents) and `claude-haiku-5-5` (guardrails, classification). No Python services.
- Frontend: TypeScript + HTML + CSS, Vite. No React/Vue/Angular. Colors, spacing and type only through CSS variables in `frontend/src/styles/tokens.css`. Beko blue `#0089CF`.
- Data: PostgreSQL (configuration, RBAC, workflows, knowledge, cost aggregates) and MongoDB (conversations, messages, runs, LLM/tool calls, searches, guardrail decisions). Every Mongo document stores `trace_id` and `span_id`.
- Observability: OpenTelemetry → Collector → Prometheus / Tempo / Loki → Grafana. Follow OTel GenAI conventions (`gen_ai.*`). No Elasticsearch.
- Runtime: Docker images for `linux/arm64`, k3s on Oracle Cloud (2 OCPU / 12 GB total for everything).

## Language

- **UI text is Turkish**: labels, messages, empty states and errors. Keep all strings in `frontend/src/i18n/tr.ts`.
- **Code, comments, identifiers, commit messages and docs for developers are English.**

## Coding rules

- Keep it simple: build what the current phase needs, no speculative abstractions.
- No secrets in code or git. Config comes from environment variables; commit only `.env.example` with placeholders.
- Cost is never hard-coded: use `PricingOptions`.
- Every agent/LLM/tool/knowledge/guardrail step must go through the shared step wrapper (span + Mongo event + SignalR + metrics). Do not bypass it.
- Tests: xUnit for backend (fake `ILlmClient`, no real API calls in tests), Vitest for frontend. Never disable or skip a test to make CI pass; report it instead.
- Never use `--no-verify` or bypass checks.

## Git

- Work on `dev` or feature branches (`feat/...`, `fix/...`, `chore/...`). Conventional commit messages (`feat(backend): ...`).
- Never push to `main`, force-push, or delete branches without the human's approval. `main` deploys to production.
- Before ending a session, update `docs/PROGRESS.md` (lead) or report your status to the lead (teammates).

## Commands

All commands run from the repo root through the `Makefile`:

| Command | What it does |
|---|---|
| `make up` / `make down` | start / stop the full local stack with Docker Compose |
| `make backend-run` | run the API locally on http://localhost:5080 |
| `make frontend-dev` | run Vite on http://localhost:5173 (proxies `/api`, `/hubs`, `/health` to :5080) |
| `make test` | backend + frontend tests |
| `make lint` | `dotnet format --verify-no-changes` + ESLint + `tsc --noEmit` |
| `make build` | build backend and frontend |
