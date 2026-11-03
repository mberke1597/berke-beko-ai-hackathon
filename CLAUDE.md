# CLAUDE.md

@AGENTS.md

## Start of every session

1. Read `docs/PROGRESS.md` (state: done, in progress, next, decisions).
2. Run `git log --oneline -20`.
3. Read `Planning/Phases.md` for the current phase and `docs/architecture/contracts.md` before touching APIs.

## Working mode

The main session is the lead/architect. Implementation is delegated to teammates that each own one area (see the ownership table in `AGENTS.md`). Verify library APIs against current official docs (.NET 10, `Anthropic` NuGet, OpenTelemetry) instead of relying on memory.
