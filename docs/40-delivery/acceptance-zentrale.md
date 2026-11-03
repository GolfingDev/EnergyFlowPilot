# Acceptance: Energy Flow Zentrale on the Raspberry Pi

Hardware acceptance of the Zentrale controls with the real devices. Unit tests cover the logic; this list covers what only the installation can show. Work through it on site, with the devices in view.

## Before you start

1. Deploy with the usual scripts in `scripts/deploy/` and back up `tibber-victron-controller.db` first.
2. Note the current values of every setting you will change (or take a screenshot of the settings page), so each step can be undone.
3. Open the Zentrale in a desktop browser and on the phone. Switch to "Zentrale Dunkel" in the design menu if another design is stored.
4. Keep the Victron and E3/DC portals open to compare measured power.

Every step: do the action, check the expected result, undo it before the next step unless stated otherwise.

## 1. Dashboard and flow

| Step | Expected result |
| --- | --- |
| Compare PV, house, grid and both batteries with the device portals | Values match within normal measurement noise; directions (import/export, charge/discharge) match |
| Wait with PV production and a charging battery | Lines PV → house and house → battery animate; no direct PV → battery line |
| Pull the network cable of the Victron (or stop MQTT) for 3 minutes | Header shows "Live-Daten veraltet", lines stop, status lists the stale data first |
| Heat pump running (compressor audible) | Heat pump line animates, node shows "Verdichter x %"; the percentage is plausible against the device display |
| Heat pump idle | Line not animated, node shows "Verdichter aus" |
| Phone, portrait | Bottom navigation, flow as a list, no horizontal scrolling |

Register 33103 is unverified: if the percentage does not follow the compressor, note the observed values and set the result in `docs/30-operations/weishaupt-warmwasser.md`.

## 2. Battery panel (click "Akku 1")

| Step | Expected result |
| --- | --- |
| Mode "Sofort laden", 1 kWh, start | Steps show sent → accepted → active → "Akku misst … Laden"; Victron portal shows charging within a minute |
| Switch back to "Automatik" | Manual charge ends; automation resumes; status line about manual charge disappears |
| Ladegrenze −5 %, Übernehmen | Steps reach "Vom Regler übernommen" within one decision interval; forecast chart shows the lower limit after the next plan |
| Reserve +5 %, Übernehmen | Same; validation refuses a reserve above the charge limit |
| Max. Ladeleistung lower, Übernehmen | Next charging phase stays below the new value |
| Viewer account | Panel shows values, no controls, hint "nur mit der Rolle Admin" |

With joint control active, "Sofort laden" charges both batteries: check both portals.

## 3. Battery panel (click "Akku 2")

| Step | Expected result |
| --- | --- |
| Without joint control | Panel states that the E3/DC regulates itself; no mode control |
| With joint control: own charge limit 1 kW | Requests to the E3/DC stay at or below 1 kW (E3/DC portal); nothing changes in the E3/DC's own settings |
| Set own limit back to 0 | Panel shows "keine"; device limits apply again |

## 4. Grid, price, strategy

| Step | Expected result |
| --- | --- |
| Netzleistungsbudget to a low value | Joint plan respects it (planned grid charge power stays below); "0" shows "keine zusätzliche Grenze" |
| Mindestvorteil higher | Fewer grid charge phases in the plan after the next calculation |
| "Befehle an die Akkus senden" off (confirm dialog) | Automation shows "Simulation"; no setpoints reach the Victron; switch back on afterwards |

## 5. Car

| Step | Expected result |
| --- | --- |
| Plug in the car | Node and key figure show "Angesteckt", car state of charge from CarData |
| "Anhalten" | Wallbox stops within half a minute; panel shows "von Hand angehalten" |
| "Fortsetzen" | Charging resumes |
| "Sofort laden bis 80 %" | Charging at full power until the target, then the plan continues |
| Automation switch off/on (confirm dialog) | Automation card shows "Automatik aus – nur Vorschau" and back |

## 6. Hot water

| Step | Expected result |
| --- | --- |
| Automation switch off/on | Automation card follows; no set point change while off |
| During a planned price window | Card shows "Anhebung aktiv" and "Preisfenster endet" with the end time |

## 7. Automations and history

| Step | Expected result |
| --- | --- |
| Open "Automationen" | Three automations (battery, car, hot water) with state, reason and next change |
| Click a row in the decision history | Inputs and thresholds appear; values match the settings at that time |
| Expert level on | Rule IDs visible in automations and history |

## Result

Record date, firmware versions and any deviation in `docs/40-delivery/backlog.md` under "Energy Flow Zentrale", item 8.
