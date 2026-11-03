# Diagrams index

All diagrams are Mermaid (they render on GitHub and in VS Code). Context: [../overview.md](../overview.md), [../architecture/architecture.md](../architecture/architecture.md).

| # | Diagram | Type | Shows |
|---|---|---|---|
| 01 | [Use cases](01-use-case.md) | flowchart | actors and what they do |
| 02 | [Components](02-component.md) | flowchart | backend, frontend and infrastructure building blocks |
| 03 | [Agent run](03-agent-run-sequence.md) | sequence | one spoken request end to end, with tracing |
| 04 | [Device command](04-device-command-sequence.md) | sequence | app / remote / AI command through mock HomeWhiz to the simulator |
| 05 | [Presence](05-presence-flow.md) | flowchart | mock location → leave range → setback → return → pre-condition |
| 06 | [Household tasks and conflicts](06-household-task-flow.md) | flowchart + sequence | task creation and fair distribution; comfort conflict resolution |
| 07 | [Deployment](07-deployment.md) | flowchart | GitHub → GHCR → Azure VM → k3s → users |
| 08 | [PostgreSQL model](08-er-postgres.md) | ER | configuration, RBAC, home domain |
| 09 | [MongoDB model](09-mongo-document-model.md) | class | event documents linked by trace |
| 10 | [Comfort decision](10-comfort-decision.md) | flowchart | comfort engine + priority mode → cheapest action |
| 11 | [Learning loop](11-learning-loop.md) | flowchart | events → pattern learner → profiles → better decisions |
