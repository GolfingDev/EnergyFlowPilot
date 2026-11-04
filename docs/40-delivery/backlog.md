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
45. Add address-based geocoding for PV forecast settings so latitude and longitude can be derived from a user-entered installation address.
46. Add multi-plane PV forecast configuration so one installation can contain multiple PV surfaces with separate azimuth, declination and kWp values.
47. Add UI text translation storage for v2, including a persistence concept for database-backed multilingual labels and help texts.
48. Rebuild the live energy view as an animated flow visualization with pictograms, directional power paths and clearly readable live values for PV, grid, battery and house.
49. Scale the consumption profile per hour with the hourly outdoor temperature instead of one factor per day, because the heat pump runs most in cold nights and mornings.
50. Revisit the consumption scale limit (0.5 to 2.0) once a winter of daily summaries exists; a frosty day after mild weeks can need more than twice the learned consumption.

## Gemeinsame Victron-/E3/DC-Steuerung

Anforderung: `docs/20-domain/battery-decision-engine.md`, Abschnitt „Gemeinsame Steuerung von Victron und E3/DC“. Diagnose und Momentaufnahme: `docs/30-operations/e3dc-rscp.md`.

1. Implementiert: nativer RSCP-Lese-/Schreibadapter mit gemeinsam genutzter Verbindung, verschluesselte Zugangsdaten (Schluesselbund neben der Datenbank), vollstaendige Rahmen, CRC und Fristen; ausschliesslich fluechtige Leistungsbefehle.
2. Implementiert: getrennte Kapazitaeten, Reserven, effektive Grenzen und zwei Energiezustaende in der wirtschaftlichen Planung; numerische dynamische Programmierung statt aggregiertem SoC.
3. Implementiert: gemeinsames Rohdaten-Verbrauchsmodell mit Temperaturkorrektur. Offen: langfristige gemeinsame Tagessummen und gemeinsame Energie-/Ersparnisabrechnung.
4. Implementiert: koordinierte Ausfuehrung, Richtungswechsel, Befehlsruecklesung, Rampen, Last-/Phasengrenzen, Schutz vor konkurrierenden Schreibern, persistierte Wiederherstellung und Linux-Prozesswatchdog (15 s, budgetierte Zyklen) mit separatem Stop-Helfer. Uebernahme erst nach erfuellten Startbedingungen; manuelle Ruecknahme-Bestaetigung als Notausgang.
5. Implementiert: Dashboard fuer beide Speicher, gemeinsame Projektion fuer bestehenden Forecast und Live-Entscheidung, getrennte Betriebsereignisse fuer Planung und Ausfuehrung.
6. Offen vor Aktivierung: bestaetigte elektrische Anschlussgrenzen, Deployment auf dem Raspberry Pi und kontrollierte Hardwareabnahme inklusive PV-/Entlademodus, Kommunikationsverlust, Prozessabbruch und Neustart. Default bleibt deaktiviert. Ein vollstaendiger Netzwerkverlust ist nicht als physisch abgesichert nachgewiesen.

## EV charging (Charge Amps Halo, BMW i4)

Requirement: `docs/20-domain/ev-charging.md`.

1. Implemented: OCPP 1.6J central system, read-only (LAN-only port 8887, sessions and meter values, metering configuration, dashboard card). Open: acceptance test with the real Halo.
2. Implemented: BMW CarData read access (device-code sign-in, encrypted tokens, MQTT stream for state of charge, car page). Open: acceptance test with the real account.
3. Implemented: manual trips with OpenStreetMap distance, weekly series on chosen weekdays, car parameters, required state of charge and minimal course per departure. Winter surcharge from the temperature forecast and skipping single occurrences of a series implemented.
4. Charging plan and control:
   - 4a Implemented: quarter-hour charging plan (trips, PV surplus from the joint plan, base state of charge), preview on the car page.
   - 4b Implemented: time-limited SetChargingProfile/ClearChargingProfile with hardware test buttons. Open: hardware test on the Halo.
   - 4c Implemented: automatic control (plan, live PV surplus with grid allowance, cheap-price charging beyond trips, full-power fallback), master switch default off. Open: activation after the hardware test.
   - 4d Implemented: planned car load in the joint battery plan; wallbox power stored with live samples and left out of the consumption model.
6. Implemented: car charging optimizer (dynamic programming, PV by value against the home batteries' marginal value, economic charging, PV scenarios). Learned consumption per km (from CarData drives) and learned PV forecast correction (shown, applied by switch) implemented. Open: check both once real data exists.
5. Implemented: calendar integration (Microsoft Graph application permission, places from location field or unambiguous subject patterns, automatic trips that can be hidden). Per-user calendars, trip owners and the Driver role implemented.
7. Implemented: "charge now or by price" below the base state of charge per charging session, installable app (PWA) with push messages. Open: test on the operator's Android phone. Open: acceptance test with the real calendar, Exchange restriction to the drivers' mailboxes, several cars.

## Energy Flow Zentrale (UI redesign)

Requirement: `docs/10-architecture/frontend-architecture.md`, section "Energy Flow Zentrale".

1. Implemented: design "Zentrale" (dark and light) with sidebar, dashboard (key figures, energy flow, status, automations, 24 h forecast, planned actions), context panel with direct battery, car and hot water control, pages Analyse, Steuerung and Automationen, settings search and app-wide expert level.
2. Implemented in part: compressor power demand (Weishaupt register 33103, percent) is read, stored and shown; the flow shows the heat pump as running without kW. Open: check the register on the device; electrical power needs a sub-meter, Modbus offers none.
3. Implemented: `GET /api/automations` (rule, state, current action, trigger text, devices, next change, command problem) for battery optimisation, car charging and hot water; the Zentrale uses it.
4. Implemented in part: every decision log entry stores the configured thresholds in force (minimum advantage, wear cost, reserve, charge limit, power limits, efficiency) next to the measured inputs; `/api/decision/logs` returns them as `inputs`, the decision history shows them on click. Open: comparison prices computed inside the rules (e.g. "later price X") would need changes in every rule and are deliberately not logged yet.
5. Implemented: the decision worker reports the start of its last completed cycle in `/api/status`; the UI shows "taken over" only after a cycle that started after the save.
6. Implemented: own E3/DC charge and discharge limits (`jointBattery.e3dc.maximumChargePowerWatts` / `...DischargePowerWatts`, 0 = none). They only lower planner and live limiter requests; nothing is written to the device, in line with "volatile commands only".
7. Implemented: review up to 7 days; week, month and year history of storage energy and savings from the stored daily summaries. Open: grid import and export per day beyond the 14-day live sample retention would need their own daily summary.
8. Partly accepted on 2026-11-04 with real devices (checklist: `docs/40-delivery/acceptance-zentrale.md`); the operator ended the session early because everything checked worked and will report errors as they show up.
   - Passed: 1 flow values against device portals, live connection, line animation, heat pump compressor percentage (register 33103 plausible while running), phone layout; 2 joint manual charge start and return to automatic, charge limit and reserve taken over (reserve above limit refused), lower charge power respected, viewer sees no controls; 3 own E3/DC charge cap of 1 kW respected and reset to 0; 4 grid power budget respected, 0 shows "keine zusätzliche Grenze".
   - Not checked: 1 stale data after a Victron outage; 3 E3/DC panel without joint control (joint control is always on); 4 minimum advantage and the dry-run switch; 5 car; 6 hot water; 7 automations and history.
   - Finding: battery 2 values in the flow come from the E3/DC cloud (polled every 60 s) and look current although they lag, e.g. still "lädt" after charging stopped. Fixed: the stored live sample takes the newest E3/DC battery reading (local RSCP of the joint execution or preview, cloud as fallback); see `docs/30-operations/e3dc-rscp.md`. To confirm on site after deployment.

## Rule

Backlog items should remain small enough to implement and verify in one focused step.
