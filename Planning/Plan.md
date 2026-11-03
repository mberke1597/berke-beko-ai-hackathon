# Plan — Beko (Arçelik) AI Hackathon: Agent Platform

<context>
We are a team competing in the **Beko (Arçelik) AI Hackathon**, where teams build AI agents for a business problem. **The problem has not been announced yet, and the hackathon starts in less than a week.** Our strategy is to build a reusable, enterprise-grade **agent platform** now. When the problem is announced, we plug one or more agents into it within hours instead of starting from zero.

Most teams will use Microsoft Copilot Studio (low-code). We are differentiating by building everything ourselves in high code, with an enterprise .NET stack and full AI governance. This is a big part of our "Innovation" and "Business Value" story, so the code quality, architecture and observability should look like something Beko could actually run in production.

The jury scores us on five criteria. Keep them in mind for every design decision:

1. **User-centric impact and benefit**: how much real value does the user get?
2. **Agent effectiveness (technical)**: does it work reliably and efficiently?
3. **Innovation and differentiation**: how different and novel is it?
4. **Business value**: business impact and feasibility. The jury does not want a small fix for a small audience; it should scale across Beko.
5. **AI safety and trust**: AI governance, transparency and control.
</context>

<goal>
Build a reusable agent platform with these capabilities. The demo agent is only a placeholder until the real problem is announced.

- **Agent registry**: agents with name, description, instructions, model, tools, and **subagents**. New agents can be added from the UI without writing code ("Add agent" screen).
- **Per-agent instructions and topic restriction**: general rules plus an allowed-topics scope; off-topic requests are politely refused.
- **Guardrails**: input and output checks (prompt injection, PII, off-topic, unwanted content), with every decision recorded.
- **Tone selector**: per agent and per conversation (e.g., resmi / samimi / teknik).
- **Role-based permissions**: at minimum Admin (everything), Agent Builder (create and edit agents), User (chat with permitted agents), Viewer (dashboards only).
- **Live workflow view**: see `<workflow_view>` below.
- **Governance dashboard**: cost, agent name and details, subagents, tool usage, knowledge searches, latency, guardrail decisions. See `<observability>`.

The governance story we will tell the jury: *"We record every step. For every chat we keep each message, every tool call, every knowledge search, every guardrail decision, its cost and its duration, and you can inspect all of it live."* Build so that this claim is literally true.
</goal>

<how_to_work>
This is a long, multi-session project. Work this way:

- **You (the main session) are the lead and architect.** Keep the big picture, the plan and the decisions in this session. Delegate implementation, coding and bug fixing to subagents or agent-team teammates so that your own context stays clean. Use agent teams for parallel, independent workstreams (e.g., backend, frontend, infra/CI). Agent teams are experimental and need `CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1` in `.claude/settings.json`; use plain subagents if that is not enabled. Start with 3 teammates. Give each one a clear spawn prompt with the context it needs, because teammates do not see this conversation. Assign each teammate its own set of files so they never edit the same file.
- **Plan first.** Before any code, read this whole file, then send me: (1) the architecture (one Mermaid component diagram is enough), (2) the repo structure, (3) the milestone and task breakdown, and (4) any questions where my answer would change the design. Wait for my approval before implementing.
- **Track state on disk** so work survives context resets: keep `docs/PROGRESS.md` (freeform notes: done, in progress, next, decisions) and use git commits as checkpoints. At the start of a new session, read `CLAUDE.md`, `docs/PROGRESS.md` and `git log` first.
- **Verify, don't assume.** Libraries move fast (.NET 10, Anthropic C# SDK, OpenTelemetry packages). Check current official docs for package names, versions and APIs instead of relying on memory.
- **Keep it simple.** We have less than a week. Prefer working, demoable features over abstractions built for hypothetical future needs. Clean Architecture is required, but do not add layers or patterns that a feature does not need.
- **If something in this plan is unclear or conflicts with reality** (e.g., a resource limit), stop and ask me rather than guessing.
</how_to_work>

<git_and_safety>
- Work freely on the `dev` branch and on feature branches: commit, push and open PRs as needed.
- **Ask me before**: merging or pushing to `main`, triggering a production deploy, force-pushing, deleting branches, dropping databases or volumes, or changing the server or Kubernetes cluster directly.
- Never commit secrets. The Anthropic API key and all credentials come from environment variables, GitHub Actions secrets, and Kubernetes Secrets. Commit only `.env.example` files with placeholders.
- Do not bypass checks (`--no-verify`, disabled tests) to make something pass. If a test is wrong, tell me.
</git_and_safety>

<tech_stack>
These decisions are fixed unless you find a hard blocker (tell me if so).

**Backend: .NET 10, C#, ASP.NET Core Web API**
- Clean Architecture: `Domain`, `Application`, `Infrastructure`, `Api` projects, plus test projects. Controllers-based Web API.
- Configuration with the Options pattern (`IOptions<T>`), validated on startup. Example: `AnthropicOptions { ApiKey, DefaultModel, MaxTokens }`, `PricingOptions` (price per million tokens per model, so cost calculation is never hard-coded).
- Agents run in **pure .NET** using the official Anthropic C# SDK (verify the current package). Do not add a Python service; the message to the jury is "enterprise-ready .NET".
- Default models (configurable per agent): `claude-sonnet-5-5` for agents, `claude-haiku-5-5` for cheap, fast tasks such as guardrail classification and tone checks.
- Real-time updates to the UI via SignalR (or SSE if simpler).

**Frontend: TypeScript + HTML + CSS** (no React, Vue or Angular), bundled with Vite.
- For the workflow graph use a vanilla JS graph library such as Cytoscape.js, or a hand-built SVG/HTML card layout if that matches the reference better.
- **The UI language is Turkish.** All labels, messages, empty states and errors are in Turkish. Code, comments and identifiers are in English.

**Data**
- **PostgreSQL**: relational and configuration data, including users, roles, permissions, agents, subagent links, instructions, tools, workflow definitions, tone presets, and cost aggregates.
- **MongoDB**: high-volume event and log documents, including conversations, messages, tool calls with inputs and outputs, knowledge searches, and guardrail decisions. Every document stores the OpenTelemetry `trace_id` and `span_id` so the dashboard can jump from a conversation to its trace.

**Infrastructure**: Docker for every service, Docker Compose for local development, Kubernetes (k3s) for the server. See `<deployment>`.
</tech_stack>

<observability>
Use **OpenTelemetry** for traces, logs and metrics, and follow the OpenTelemetry GenAI semantic conventions (`gen_ai.*` attributes) where they exist.

- **Pipeline**: .NET app → OpenTelemetry Collector → **Prometheus** (metrics), **Tempo** (traces), **Loki** (logs) → **Grafana**. This stack was chosen because it fits the Oracle free-tier server. Elasticsearch was considered but is too memory-hungry; do not deploy it.
- **Spans** for every agent run, subagent call, LLM call, tool call, knowledge search, and guardrail check.
- **Metrics** (at minimum): input and output tokens, **LLM cost** per agent, user, and model; latency per step; tool call counts and errors; guardrail blocks by reason; active conversations.
- **Dashboards as code**: provision Grafana datasources and dashboards from files in `monitoring/grafana/` so they appear automatically on deploy.
- The custom web dashboard (our TypeScript UI) shows the agent-centric view: agents, subagents, workflow, cost, and tool usage. It links into Grafana for deep technical drill-down. Grafana and the custom dashboard complement each other.
</observability>

<workflow_view>
The workflow page is our visual centerpiece. Take inspiration from the reference screenshot at `docs/reference/workflow-reference.png` (Microsoft Power Automate style). It shows a vertical chain of rounded white step cards on a dotted background, connected by short arrows. Each card has an icon, a step title (e.g., "When an agent calls the flow", "Get file content using path", "Create file", "Respond to the agent") and a "⋯" menu. A circular "+" button between cards inserts a new step.

Requirements:
- A workflow is **data, not code**. Store its definition (steps, order, step type, configuration) in PostgreSQL, and have the agent runtime execute that definition. Editing the workflow in the UI (add, remove, reorder, or configure a step) changes how the agent runs on the next request, with no redeploy.
- **Live execution**: while an agent runs, highlight the active step and show status (bekliyor / çalışıyor / tamamlandı / hata), duration and cost per step, pushed in real time.
- Show subagents as nested or branching steps.
- Clicking a step shows its details: inputs and outputs, tool calls, tokens, cost, and a link to its trace.
- Before building, research how others implement dynamic, data-driven agent workflows and editable flow UIs, and propose the approach to me in the plan.
</workflow_view>

<branding>
This is a Beko competition, so the UI must look like a Beko product.
- Primary color: **Beko blue `#0089CF`**, on white backgrounds, with neutral greys. Use blue for primary actions, active states and highlights, and keep the UI clean and bright, matching Beko's style.
- Logo: `frontend/public/brand/beko-logo.png` (transparent background). Use it in the header and on the login screen. Keep its proportions and do not recolor or distort it.
- Define colors, spacing and typography as CSS variables in one design-tokens file so the theme stays consistent.
</branding>

<repo_structure>
Model the repo layout and documentation quality on https://github.com/chyp3r/KACHOW-Teknofest-2026 (clone it to study). Adapt it to .NET; roughly:

```text
backend/            .NET solution (src/Domain, src/Application, src/Infrastructure, src/Api, tests/)
frontend/           Vite + TypeScript app (src/pages, src/components, src/api, src/styles, public/brand)
deploy/docker/      Dockerfiles (multi-stage, linux/arm64)
deploy/kubernetes/  k3s manifests (namespace, configmap, secrets template, deployments, services, ingress)
monitoring/         otel-collector, prometheus, tempo, loki, grafana provisioning + dashboards
docs/architecture/  architecture.md, backend.md, frontend.md, ai.md
docs/diagrams/      numbered Mermaid diagrams + 00-index.md
docs/development/   coding standards, git workflow, testing
docs/reference/     reference images
evaluation/         small test sets for the agents (intents, guardrail attacks, tool use)
.github/workflows/  CI/CD pipelines
compose.yml, Makefile, .env.example, README.md, CLAUDE.md, AGENTS.md
```

- **`CLAUDE.md`** (read automatically by Claude Code and by every teammate): project rules, commands to build, test and run, and pointers to the docs. If you also keep an `AGENTS.md` like KACHOW, import it from `CLAUDE.md` with `@AGENTS.md` so the rules live in one place.
- **Diagrams** in Mermaid under `docs/diagrams/`, numbered like KACHOW: use case, sequence diagram of an agent run (with guardrails, tools and tracing), activity, deployment (k3s on Oracle), component, ER (PostgreSQL), and the MongoDB document model. Diagrams must use the real module and table names from the code.
- **README**: centered header, one-sentence pitch, categorized technology badges, architecture diagram, screenshots, and quick-start commands.
</repo_structure>

<deployment>
- **Server**: Oracle Cloud Free Tier, Ampere A1 VM (**ARM64**). Verify the current always-free limits, and size memory requests and limits for all pods so everything fits.
- **Cluster**: k3s (lightweight Kubernetes) on that VM, used for the demo and presentation.
- **Repo**: https://github.com/mberke1597/berke-beko-ai-hackathon.git
- **CI/CD with GitHub Actions**:
  - On PRs and pushes to `dev`: build, lint and test backend and frontend.
  - On push to `main`: build Docker images **for `linux/arm64`** (buildx), push them to GitHub Container Registry (GHCR), then deploy to k3s (kubeconfig or SSH key from GitHub secrets) and wait for the rollout to finish.
- **Deploy early**: get a "hello world" frontend and backend through the full pipeline to the server on day 1, so deployment problems never surprise us on the last day.
- List every manual step I must do myself (create the VM, open ports in the Oracle security list and the VM firewall, install k3s, add GitHub secrets, set up DNS or TLS) in `docs/deployment/oracle-k3s.md`, with exact commands.
</deployment>

<milestones>
Order by risk and demo value. Each milestone ends with something running and committed.

1. **Skeleton + pipeline**: repo structure, `CLAUDE.md`, .NET solution, Vite app, Compose, and CI/CD deploying hello world to k3s.
2. **Agent runtime + tracing**: Anthropic SDK integration, one demo agent with one tool and one subagent, full OpenTelemetry spans, MongoDB logging, and cost calculation.
3. **Dashboard + live workflow view**: Beko-branded Turkish UI, agent list and details, cost, tool usage, and the live workflow page.
4. **Governance**: guardrails, topic restriction, tone selector, RBAC, and Grafana dashboards.
5. **Add-agent screen + dynamic workflow editing.**
6. **Docs, diagrams, README, evaluation set, demo script.**

If time runs short, cut from milestone 5 first. Never cut tracing and cost, because they are the core of our governance story.
</milestones>

<when_the_problem_arrives>
When I give you the hackathon problem statement, start by proposing which agents, subagents, tools and knowledge sources solve it, and how they map to the five jury criteria. Then build them on the platform without changing the platform's core.
</when_the_problem_arrives>