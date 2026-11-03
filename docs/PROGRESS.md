# Progress

## Current phase
Phase 1 — Skeleton + pipeline (started 2026-11-03). Phase 0 (Oracle VM) runs in parallel with the human.

## Done
- 2026-11-03: Plan approved (`Planning/Proposal.md`, `Planning/Phases.md`). `AGENTS.md`, `CLAUDE.md`, `docs/architecture/contracts.md` (Phase 1) written.
- Oracle account signed up (home region chosen by the human). SSH key `~/.ssh/beko-oci` created on the human's machine (never commit).

## In progress
- Phase 1: backend, frontend, infra teammates working on separate branches.
- 2026-11-03: Real problem received: **Çeliknaz** voice home-comfort assistant (mock HomeWhiz AC). Project document `Planning/Celiknaz.md` and UI spec `Planning/Celiknaz-UI.md` (3 tabs: Evim, Görevler, Asistan) approved for development. Docs: `docs/overview.md`, `docs/architecture/architecture.md`, `docs/diagrams/00–11`.
- Phase 0: human is creating the Oracle A1 VM (Milan).

## Next
- Phase 0: create A1 VM, open ports, install k3s + cert-manager, add GitHub secrets.
- First deploy to `main` (needs human approval).

## Decisions
- Repo public → native `ubuntu-24.04-arm` runners for arm64 images.
- No domain → `<ip>.sslip.io` + Let's Encrypt.
- Users table in Postgres is the login allow-list; local e-mail + password + JWT; Entra ID can be added later.
- Workflow = ordered, nestable steps stored in Postgres, versioned copy-on-write; hand-built HTML/CSS card UI.
- Knowledge search = Postgres full-text search (turkish); no vector DB for now.
- Oracle Always Free A1 = 2 OCPU / 12 GB → all pods have memory limits (budget in Proposal §5).
