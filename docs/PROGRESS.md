# Progress

## Current phase
Phase 1 — Skeleton + pipeline (started 2026-11-03). Phase 0 (Oracle VM) runs in parallel with the human.

## Done
- 2026-11-03: Plan approved (`Planning/Proposal.md`, `Planning/Phases.md`). `AGENTS.md`, `CLAUDE.md`, `docs/architecture/contracts.md` (Phase 1) written.
- Oracle account signed up (home region chosen by the human). SSH key `~/.ssh/beko-oci` created on the human's machine (never commit).

## In progress
- Phase 1: backend, frontend, infra teammates working on separate branches.

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
