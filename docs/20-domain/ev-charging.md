# EV Charging (Charge Amps Halo, BMW i4)

Status: requirements and staged plan agreed with the operator on 2026-10-31. Stage 1 (OCPP, read-only) implemented.

## Goal

Central control of electric vehicle charging together with the two home batteries: charge the car in cheap quarter hours and with PV surplus so that every planned trip can be driven, and let the home batteries supply the car only when that is cheaper than grid electricity.

## Site facts

- Wallbox: Charge Amps Halo, three-phase, 11 kW (16 A per phase). The minimum while charging is 6 A per phase (about 4.1 kW); the Halo does not switch to single-phase, so PV-only charging needs about 4.1 kW surplus.
- The house phase L1 including the wallbox share is supplied through the Victron AC-out (see `battery-decision-engine.md`). Car charging on L1 therefore counts against the Victron AC input limit; L2/L3 come directly from the grid.
- Car: BMW i4. BMW blocked third-party commands on 2025-09-29; the official BMW CarData interface is read-only. **The car cannot be commanded; charging is controlled at the wallbox only.**

## Operator decisions (2026-10-31)

| Topic | Decision |
|---|---|
| Wallbox interface | Local OCPP 1.6J with the controller as central system. The Charge Amps app/cloud is not needed and will be lost. |
| Home batteries and the car | Allowed when necessary and cheaper than grid electricity. |
| Car state of charge | BMW CarData (official, read-only), streamed live. |
| Demand | Manual trip planning first: where to, when, including recurring trips. The controller derives the required state of charge per departure. |
| Distances | Address entry; distance from OpenStreetMap geocoding and routing, stored and editable. |
| Car connected, no trip planned | Always use PV surplus; additionally charge in the cheapest quarter hours up to a configurable base state of charge (e.g. 50 %). |
| Later stage | Derive trips from the operator's calendar. |

## Rules

1. **Never stand still for missing data** (operator rule 2026-10-29). If prices, PV forecast, car state of charge or the plan are missing while a trip is due, charge at full power so the car is ready. If the wallbox connection is lost, the Halo keeps its last limit; this must be checked on the hardware.
2. The required state of charge for a departure = energy for the trip (outbound and return unless marked one-way) at the configured consumption, plus an arrival reserve, plus a winter surcharge from the temperature forecast. It is capped at the car's charge limit.
3. Charging is planned into quarter hours by effective cost: PV surplus first, then the cheapest grid quarter hours before the departure.
4. Planned car charging enters the joint battery plan as consumption. The joint planner then decides, as for any house load, whether the home batteries cover it (only when their stored energy is worth less than buying from the grid at that time).
5. Live limits stay in force: phase and Victron input limits include the car load; car charging never forces grid charging of the home batteries.
6. The OCPP endpoint is reachable from the home network only, never through the public Tailscale Funnel address, and accepts only the configured charge point id.

## Staged plan

Each stage is deployable and testable on its own.

1. **OCPP central system, read-only.** WebSocket endpoint for the Halo (`ws://<pi>:<port>/ocpp/<chargePointId>`, LAN only). Handles BootNotification, Heartbeat, StatusNotification, Authorize, Start/StopTransaction and MeterValues; persists sessions and meter values. Dashboard shows car connected, charging state, power and session energy. No commands yet. On the Halo: local web interface (hotspot `HALO_<serial>`, `192.168.250.1`) → OCPP → backend URL. Charge Amps documents firmware 158 or newer for third-party OCPP.
2. **BMW CarData read access.** One-time device-code sign-in from the settings page; tokens stored encrypted; MQTT stream (`customer.streaming-cardata.bmwgroup.com`, topic `<gcid>/<VIN>`) for state of charge, charge limit, charging state. Shown on the dashboard.
3. **Destinations and trips.** Destinations by address (geocoding and routing via OpenStreetMap services, editable distance); one-off and weekly recurring trips; car parameters (usable capacity, consumption, arrival reserve, base state of charge). Shows the required state of charge per upcoming departure.
4. **Charging plan and control.** Quarter-hour charging plan per rules 2 and 3; current set via OCPP SetChargingProfile (0 A pauses); planned load fed into the joint battery plan; fallback per rule 1. Requires a hardware test whether this Halo firmware accepts charging profiles during a running transaction.
5. **Calendar.** Trips derived from calendar appointments with a location; calendar source to be decided.

## Stage 1 implementation (OCPP central system)

- **Listener:** separate Kestrel port `EvCharging:OcppListenPort` (default 8887, `appsettings.json`; 0 disables it), bound to `0.0.0.0` next to the regular nginx listener. Tailscale Funnel only publishes port 80, so the port is not public. A middleware isolates the two ports: the OCPP port serves only `/ocpp/...`, and `/ocpp` is refused on the regular port (and thus through nginx and Funnel).
- **Admission:** `ev.ocpp.enabled` must be true and the charge point id (last URL segment) must equal `ev.ocpp.chargePointId`. Rejected attempts are logged and shown on the dashboard (admin only) with the id, so the operator can copy it into the settings. Subprotocol `ocpp1.6`.
- **Messages:** BootNotification (Accepted, heartbeat 60 s), Heartbeat, StatusNotification, Authorize (every id tag accepted: stage 1 never blocks charging), Start/StopTransaction, MeterValues, DataTransfer (UnknownVendorId), firmware/diagnostics notifications. Other actions get `NotImplemented`; schema violations get `FormationViolation`; processing errors get `InternalError` so the wallbox retries.
- **Writes to the wallbox (metering only):** after the first message, `GetConfiguration`; if needed `ChangeConfiguration` of `MeterValueSampleInterval` (10 s) and `MeterValuesSampledData` (energy, power, phase currents, with a fallback list); then `TriggerMessage` for the current status and meter values. No charging profile, no remote start or stop.
- **Data:** sessions in table `EvChargingSessions` (AUTOINCREMENT transaction ids, duplicate starts deduplicated, a start on a connector closes a session whose stop was lost). The meter reading is persisted at most once a minute; live values (status, power, phase currents, session energy) live in memory. Power is derived from the energy register when the wallbox does not report it. Implausible wallbox timestamps (more than one day off) are replaced by the receive time.
- **Dashboard:** wallbox card (hidden while `ev.ocpp.enabled` is false) with state, power, session energy, phase currents, last five sessions; for admins also the backend addresses of the Pi and the wallbox configuration (keys containing passwords, secrets, tokens or the authorization key are hidden). API: `GET /api/ev-charging/status`.

### Setup

1. Settings → Verbindungen → Wallbox (OCPP): switch on. The dashboard card then shows the backend address of the Pi, e.g. `ws://192.168.x.y:8887/ocpp/`.
2. Halo: connect to its hotspot `HALO_<serial>` (password from the letter supplied with the Halo), open `192.168.250.1`, OCPP settings → backend URL from step 1. Check the firmware version (158 or newer).
3. The Halo connects and is rejected at first; the card shows its id. Enter that id in the setting "Ladepunkt-ID der Wallbox". The Halo retries by itself and is then accepted.

## Open points

- Halo firmware version and whether it accepts `TxDefaultProfile` changes during a transaction (stage 4).
- Exact i4 variant (usable battery capacity) and the operator's typical consumption.
- Calendar source for stage 5.
