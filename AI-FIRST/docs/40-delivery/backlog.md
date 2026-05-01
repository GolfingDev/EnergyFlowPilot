# Backlog

## Initial Tasks

1. Create solution and project structure.
2. Capture project requirements in `docs/`.
3. Define deterministic forecast input interfaces and manual test doubles for Tibber prices, weather/PV forecast and historical consumption.
4. Define first domain types for battery state, prices, telemetry and decisions.
5. Define first Battery Decision Engine interfaces in `Business`.
6. Write first Battery Decision Engine tests.
7. Implement minimal Battery Decision Engine.
8. Add structured decision reasons.
9. Add battery capacity configuration contract.
10. Add battery capacity, minimum state of charge, maximum power and efficiency parameters for cost optimization.
11. Add EF Core SQLite persistence skeleton.
12. Persist frontend-editable controller configuration in SQLite.
13. Seed SQLite with known default settings when the database is created.
14. Add startup migration/repair that adds newly introduced default settings without overwriting user values.
15. Persist required access data in SQLite with sensitivity metadata.
16. Add DTO-based API contracts for reading/updating settings without exposing secrets.
17. Add decision logging repository.
18. Add first API status endpoint backed by application state.
19. Add forecast state-of-charge projection that updates battery state slot by slot.
20. Add charge-window optimizer that uses SOC, configured capacity and configured maximum charge power to select the cheapest required 15-minute grid-charging slots.
21. Add realtime decision logging with configurable retention cleanup.
22. Add realtime discharge decision path that clamps target power to measured grid import so battery feed-in into the grid is impossible.
23. Add realtime export absorption decision path that charges from negative grid import unless preserving headroom for future negative-price charging is economically better than configured feed-in compensation.

## Rule

Backlog items should remain small enough to implement and verify in one focused step.
