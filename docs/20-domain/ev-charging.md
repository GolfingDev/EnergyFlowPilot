# EV Charging (Charge Amps Halo, BMW i4)

Status: requirements and staged plan, agreed with the operator on 2026-10-31. Nothing implemented yet.

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

## Open points

- Halo firmware version and whether it accepts `TxDefaultProfile` changes during a transaction (stage 4).
- Exact i4 variant (usable battery capacity) and the operator's typical consumption.
- Calendar source for stage 5.
