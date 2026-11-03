# Phased plan — Beko AI Agent Platform

Companion to [Proposal.md](Proposal.md) (architecture and decisions). This file is the execution order.

**Owners**
- **You**: accounts, payments, secrets, anything in a web console (Oracle, GitHub settings, Anthropic).
- **Lead** (main Claude session): docs, contracts, reviews, integration, PROGRESS.md.
- **BE** teammate: `backend/` · **FE** teammate: `frontend/` · **INFRA** teammate: `deploy/`, `monitoring/`, `.github/`, `compose.yml`, `Makefile`, `.env.example`.

Every phase ends with: something running, a commit on `dev`, and an updated `docs/PROGRESS.md`.

**Priority if time runs short**: Phase 5 is cut first. Phases 1–2 (pipeline, tracing, cost) are never cut.

---

## Phase 0 — Accounts and server (together, ~2 h, runs alongside Phase 1)

Goal: a reachable ARM VM with k3s, and all secrets in place.

| # | Task | Owner |
|---|---|---|
| 0.1 | Finish the Oracle sign-up (home region Milan or Stockholm) | You |
| 0.2 | Create the A1 VM (Ubuntu 24.04 ARM, 2 OCPU / 12 GB, 100 GB boot volume), download its SSH key | You, guided |
| 0.3 | Open ports 22/80/443 in the Oracle security list and in the VM's iptables | You, guided |
| 0.4 | Install k3s (with Traefik) and cert-manager, check `kubectl get nodes` | You, guided (commands from Lead) |
| 0.5 | Create a deploy user + SSH key for GitHub Actions | You, guided |
| 0.6 | Add GitHub secrets: `ANTHROPIC_API_KEY`, `DEPLOY_SSH_KEY`, `DEPLOY_HOST`, `DEPLOY_USER`, `JWT_SIGNING_KEY`, DB passwords | You |
| 0.7 | Record every command in `docs/deployment/oracle-k3s.md` | Lead |

**Done when**: `ssh` works, `kubectl get nodes` shows `Ready`, `http://<ip>.sslip.io` answers (404 from Traefik is fine).

---

## Phase 1 — Skeleton + pipeline (Day 1)

Goal: hello-world frontend + backend deployed to the VM through CI/CD. No deployment surprises later.

| # | Task | Owner |
|---|---|---|
| 1.1 | `CLAUDE.md`, `AGENTS.md`, `docs/PROGRESS.md`, `docs/development/*` (standards, git flow, testing) | Lead |
| 1.2 | `docs/architecture/contracts.md`: REST endpoints + SignalR event shapes (lets BE and FE work in parallel) | Lead |
| 1.3 | .NET 10 solution: Domain / Application / Infrastructure / Api + test projects, central package versions, Options with startup validation, `/health`, `/api/version` | BE |
| 1.4 | OpenTelemetry wired (traces, metrics, logs → OTLP), Postgres (EF Core) + Mongo connections with health checks | BE |
| 1.5 | Vite + TS + ESLint, `tokens.css` (Beko blue `#0089CF`), header with logo, login page shell, `i18n/tr.ts`, calls `/api/version` | FE |
| 1.6 | Multi-stage arm64 Dockerfiles, `compose.yml` (app + Postgres + Mongo + full observability stack), `Makefile`, `.env.example` | INFRA |
| 1.7 | k8s manifests (namespace, configmap, secrets template, Postgres, Mongo, backend, frontend, Traefik ingress + TLS) with memory requests/limits | INFRA |
| 1.8 | `ci.yml` (PR + `dev`: build, lint, test) and `deploy.yml` (`main`: build arm64 on native ARM runners → GHCR → SSH deploy → `kubectl rollout status`) | INFRA |
| 1.9 | First merge to `main` (**asks you first**) → watch the deploy | Lead + You |

**Done when**: `https://<ip>.sslip.io` shows the Beko login shell and the backend version; Grafana opens; `make up` runs everything locally.

---

## Phase 2 — Agent runtime + tracing (Day 2)

Goal: a real chat with Claude where every step is traced, logged and costed.

| # | Task | Owner |
|---|---|---|
| 2.1 | Domain entities + EF migrations: agents, subagent links, tools, tone presets, workflow definitions/steps, knowledge chunks, users/roles/permissions, cost_daily | BE |
| 2.2 | Seed data: demo agent "Beko Asistan", subagent "Ürün Uzmanı", tool `get_product_info`, a few knowledge chunks, 4 demo users (one per role) | BE |
| 2.3 | `AnthropicLlmClient`: streaming, tool-use loop, usage → `PricingOptions` cost | BE |
| 2.4 | `WorkflowEngine` + step executors: trigger, knowledge.search (Postgres FTS), agent.llm, subagent.call, tool.call, respond | BE |
| 2.5 | `RunEventSink`: one wrapper per step → OTel span (`gen_ai.*`), Mongo document (`trace_id`/`span_id`), SignalR event, metrics | BE |
| 2.6 | Chat API + streaming; unit tests with a fake `ILlmClient` | BE |
| 2.7 | Basic chat page (send, stream, show answer) against the contract | FE |
| 2.8 | OTel Collector → Prometheus / Tempo / Loki config, Grafana datasources provisioned | INFRA |
| 2.9 | Review: run a chat, check one full trace in Tempo and all Mongo collections | Lead |

**Done when**: one chat message produces a nested trace (agent → LLM → tool → subagent) in Grafana, matching Mongo documents, and a cost value.

---

## Phase 3 — Dashboard + live workflow view (Day 3)

Goal: the visual centerpiece and the agent-centric dashboard.

| # | Task | Owner |
|---|---|---|
| 3.1 | Dashboard query endpoints: cost by agent/model/user/day, tool usage, latency, guardrail decisions, conversation list + detail | BE |
| 3.2 | Run/step query endpoints for the workflow view (with step inputs/outputs, tokens, cost, trace link) | BE |
| 3.3 | App layout (sidebar, header), agent list + agent detail (subagents, tools, instructions) | FE |
| 3.4 | Live workflow page: dotted background, step cards, "+" between cards, statuses bekliyor / çalışıyor / tamamlandı / hata, duration and cost per step, nested subagent branch, step detail drawer with "Trace'i Grafana'da aç" | FE |
| 3.5 | Governance dashboard: stat tiles, cost charts, tool usage table, conversation explorer | FE |
| 3.6 | Prometheus metrics exposed for Grafana (tokens, cost, latency, tool errors, active conversations) | BE + INFRA |

**Done when**: during a chat, the workflow page lights up step by step in real time, and the dashboard shows the run's cost and tool calls.

---

## Phase 4 — Governance (Day 4)

Goal: the AI safety and trust story.

| # | Task | Owner |
|---|---|---|
| 4.1 | Input guardrail: regex/checksum PII (TCKN, IBAN, phone, e-mail) + Haiku classifier (injection, off-topic, harmful) | BE |
| 4.2 | Output guardrail: PII redaction + Haiku topic/tone check; polite Turkish refusals | BE |
| 4.3 | Topic restriction per agent; tone presets (resmi / samimi / teknik) per agent and per conversation | BE |
| 4.4 | Auth: `users` table as allow-list, BCrypt, JWT, permission policies, agent access per user; audit log of config changes | BE |
| 4.5 | Login flow, role-aware menu, tone selector in chat, guardrail decision badges in chat and workflow, Kullanıcılar page (admin) | FE |
| 4.6 | Grafana dashboards as code: LLM cost, latency, guardrails, runtime health | INFRA |
| 4.7 | Review: try an injection, a PII message and an off-topic question; check each is blocked, explained and recorded | Lead |

**Done when**: the 4 demo users see different menus; attacks are blocked and visible on the dashboard and in Grafana.

---

## Phase 5 — Add agent + workflow editing (Day 5, cut first)

Goal: "new agent with no code" and "edit the flow, it runs differently next time".

| # | Task | Owner |
|---|---|---|
| 5.1 | Agent CRUD API with validation; workflow definition save = new version, validation rules | BE |
| 5.2 | "Ajan Ekle / Düzenle" form: name, description, instructions, allowed topics, model, tone, tools, subagents | FE |
| 5.3 | Workflow editor: add / remove / reorder / configure steps, version history | FE |
| 5.4 | Review: create an agent in the UI, chat with it, change its flow, chat again, see the difference | Lead |

**Done when**: the full flow in 5.4 works without a redeploy.

---

## Phase 6 — Docs, evaluation, demo (Day 5–6)

Goal: everything the jury sees outside the live demo.

| # | Task | Owner |
|---|---|---|
| 6.1 | Diagrams 00–08 (use case, agent-run sequence, activity, deployment, component, ER, Mongo model) with real names | Lead |
| 6.2 | `docs/architecture/*.md`, README (badges, architecture, screenshots, quick start) | Lead |
| 6.3 | `evaluation/`: intents, guardrail attacks, tool-use sets + runner; publish the scores | BE + Lead |
| 6.4 | Load/smoke test on the VM; check memory stays within limits | INFRA |
| 6.5 | Demo script (5 minutes, mapped to the 5 jury criteria) + final deploy | Lead + You |

**Done when**: a fresh person can follow the README, and the demo runs end to end on the VM.

---

## Phase 7 — Hackathon day: plug in the real problem

1. You share the problem statement.
2. Lead proposes agents, subagents, tools and knowledge sources, mapped to the 5 jury criteria (≈30 min).
3. After your approval: new tools and seed data (BE), knowledge import, agents created through the UI, workflows edited in the editor. The platform core stays unchanged.
4. Evaluation set for the new agents, updated demo script, deploy.
