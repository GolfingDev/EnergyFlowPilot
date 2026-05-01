# Project Documentation

This folder is the source of truth for project decisions, user requirements and implementation constraints.

When a requirement in this folder changes, future code changes must follow the updated requirement. If code already exists and conflicts with these documents, the conflict should be made explicit before implementation continues.

## Reading Order

1. `00-governance/working-agreement.md`
2. `00-governance/requirement-handling.md`
3. `00-governance/coding-standards.md`
4. `10-architecture/target-architecture.md`
5. `10-architecture/project-structure.md`
6. `20-domain/battery-decision-engine.md`
7. `20-domain/time-and-observability.md`
8. `30-operations/security-and-deployment.md`
9. `30-operations/configuration-and-access-data.md`
10. `30-operations/tibber-api.md`
11. `40-delivery/testing-strategy.md`
12. `40-delivery/backlog.md`

## Rule

Before implementing non-trivial code, check the relevant documents here first and update them when the requested behavior changes the project direction.
