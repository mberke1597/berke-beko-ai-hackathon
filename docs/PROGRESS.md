# Progress

## Current phase
Phase 1 — Skeleton + pipeline (started 2026-11-03), with Çeliknaz slice 1 built in parallel. Phase 0 (Azure VM) runs in parallel with the human. Phase 2 (agent runtime + Çeliknaz chat) started.

## Done
- 2026-11-03: Plan approved (`Planning/Proposal.md`, `Planning/Phases.md`). `AGENTS.md`, `CLAUDE.md`, `docs/architecture/contracts.md` (Phase 1) written.
- SSH key `~/.ssh/beko-oci` created on the human's machine (never commit).
- 2026-11-03: Real problem received: **Çeliknaz** voice home assistant in a HomeWhiz-compatible app. Design `Planning/Celiknaz.md`, UI spec `Planning/Celiknaz-UI.md` (tabs Evim, Görevler, Asistan), docs `docs/overview.md`, `docs/architecture/architecture.md`, `docs/diagrams/00–11`.
- 2026-11-03: Frontend Phase 1 skeleton merged to `dev`.
- 2026-11-03: Backend Phase 1 skeleton + Çeliknaz slice 1 merged to `dev` (mock HomeWhiz, simulators, `/api/homes/me`, device commands with Mongo `device_events`, `DeviceHub`). 75 tests pass, format clean.
- 2026-11-03: Frontend Çeliknaz slice 1 merged to `dev` (`#/ev` shell with 3 tabs, Evim room/appliance cards, appliance details, virtual remote, live SignalR, voice overlay, mock mode `npm run dev:mock`). Lint, tsc, 92 tests, build pass.

## In progress
- Infra: k8s manifests, CI/CD, compose, `docs/deployment/oracle-k3s.md` (branch `worktree-agent-adbceebcd723bb999`).
- 2026-11-03: Phase 2 started: backend agent runtime + Çeliknaz agent (get_home_status, control_device); frontend Asistan chat, voice, "Nasıl karar verdi?" timeline. Contract: contracts.md "Phase 2".
- Infra retargeted to Azure (docs/deployment/azure-k3s.md).

## Next
- Phase 0 (Azure): confirm region + VM size with the human, create VM with static IP + DNS label, NSG 80/443, install k3s + cert-manager, add GitHub secrets. First deploy to `main` (needs human approval).
- Phase 2: agent runtime + tracing (Çeliknaz agent depends on it).
- Çeliknaz slice 2 (contract first): outdoor weather + home priority mode endpoint (frontend status strip uses constants now); program display names in `capabilities.programs`; settings, comfort engine, tasks backend.
- HomeWhiz screenshots from the human to re-skin the UI via `tokens.css`.

## Decisions
- Repo public → native `ubuntu-24.04-arm` runners for arm64 images.
- No domain → Azure public IP DNS label `<label>.<region>.cloudapp.azure.com` + Let's Encrypt (sslip.io fallback).
- Users table in Postgres is the login allow-list; local e-mail + password + JWT; Entra ID can be added later.
- Workflow = ordered, nestable steps stored in Postgres, versioned copy-on-write; hand-built HTML/CSS card UI.
- Knowledge search = Postgres full-text search (turkish); no vector DB for now.
- 2026-11-03: Oracle dropped (A1 out of capacity). Hosting = one Azure Pay-As-You-Go Linux VM with k3s; pods keep memory limits (Proposal §5 budget).
- Çeliknaz: mock HomeWhiz reached only through `IHomeWhizClient` (`HomeWhiz__BaseUrl`); simulator time runs `Simulation__TimeScale`× faster; 422 errors carry a stable `code`; household conflicts default to compromise; tasks distributed `fair_share` by default.
