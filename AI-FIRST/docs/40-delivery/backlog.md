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
24. Expose Battery Decision Engine forecast result through DTO-based API endpoint.
25. Replace live forecast test console output with an assertion-based integration scenario using the forecast simulator.
26. Register forecast service and forecast input providers in ASP.NET Core dependency injection.
27. Add frontend-editable PV forecast settings for location, peak power, module declination, module azimuth and provider selection.
28. Add optional Solcast provider for higher-quality PV forecasts once access data is configured.
29. Add DTO-based `GET /api/forecast` endpoint.
30. Add production DI registration for forecast service after SQLite settings, battery state and consumption providers exist.
31. Add DB-backed battery configuration provider for forecasts.
32. Add explicit temporary SOC provider until Victron telemetry is connected.
33. Add average daily consumption forecast provider with Europe/Berlin local profile.
34. Add Decision Audit Harness concept and business implementation.
35. Add Golden Scenario tests for 24h/96-slot decision review.
36. Add Decision Audit invariant and metamorphic tests.
37. Add CSV and JSON audit export for user-readable challenge reports.
38. Generate local Decision Audit artifacts for manual review.
39. Add daily battery savings accounting for charge/discharge reporting.
40. Persist daily battery savings summaries in SQLite.
41. Add API for daily, weekly, monthly, yearly and total battery savings.
42. Add frontend views for daily, weekly, monthly, yearly and total battery savings.
43. Add live telemetry pre-check for every real control decision, including current SOC, current consumption/grid import, PV production, Tibber price freshness and explicit idle logging when inputs are missing or stale.
44. Add a worker-process exception handler that sends a clearly formatted and unambiguous failure email for production runtime errors in scheduled jobs or realtime control loops.

## Rule

Backlog items should remain small enough to implement and verify in one focused step.
