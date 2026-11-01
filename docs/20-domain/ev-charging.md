# EV Charging (Charge Amps Halo, BMW i4)

Status: requirements and staged plan agreed with the operator on 2026-10-31. Stages 1 (OCPP, read-only), 2 (BMW CarData, read-only) 3 (manual trip planning) and 5 (calendar) implemented; stage 4 (charging control) is next.

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

- **Listener:** separate Kestrel port `EvCharging:OcppListenPort` (default 8887, `appsettings.json`; 0 disables it), bound to `0.0.0.0` next to the regular nginx listener. Tailscale Funnel only publishes port 80, so the port is not public. A middleware isolates the two ports: every path on the OCPP port is treated as OCPP (so `ws://pi:8887/ocpp/<id>` and `ws://pi:8887/<id>` both work; the last segment is the id), and `/ocpp` is refused on the regular port (and thus through nginx and Funnel).
- **Admission:** `ev.ocpp.enabled` must be true and the charge point id (last URL segment) must equal `ev.ocpp.chargePointId`. Every refused attempt on the OCPP port (wrong id, switched off, plain HTTP instead of WebSocket) is logged and shown on the car page (admin only) with time, sender address, requested path and id, so the operator can copy the id into the settings. Without any attempt the page says since when nothing has arrived and lists the usual causes. Subprotocol `ocpp1.6`.
- **Messages:** BootNotification (Accepted, heartbeat 60 s), Heartbeat, StatusNotification, Authorize (every id tag accepted: stage 1 never blocks charging), Start/StopTransaction, MeterValues, DataTransfer (UnknownVendorId), firmware/diagnostics notifications. Other actions get `NotImplemented`; schema violations get `FormationViolation`; processing errors get `InternalError` so the wallbox retries.
- **Writes to the wallbox (metering only):** after the first message, `GetConfiguration`; if needed `ChangeConfiguration` of `MeterValueSampleInterval` (10 s) and `MeterValuesSampledData` (energy, power, phase currents, with a fallback list); then `TriggerMessage` for the current status and meter values. No charging profile, no remote start or stop.
- **Data:** sessions in table `EvChargingSessions` (AUTOINCREMENT transaction ids, duplicate starts deduplicated, a start on a connector closes a session whose stop was lost). The meter reading is persisted at most once a minute; live values (status, power, phase currents, session energy) live in memory. Power is derived from the energy register when the wallbox does not report it. Implausible wallbox timestamps (more than one day off) are replaced by the receive time.
- **Car page (menu "Auto", `/car`):** wallbox card with state, power, session energy, phase currents, last five sessions; for admins also the backend addresses of the Pi and the wallbox configuration (keys containing passwords, secrets, tokens or the authorization key are hidden). API: `GET /api/ev-charging/status`.

### Setup

1. Settings → Verbindungen → Wallbox (OCPP): switch on. The car page (menu "Auto") then shows the backend address of the Pi, e.g. `ws://192.168.x.y:8887/ocpp/`.
2. Halo: connect to its hotspot `HALO_<serial>` (password from the letter supplied with the Halo), open `192.168.250.1`, OCPP settings → backend URL from step 1. Check the firmware version (158 or newer).
3. The Halo connects and is rejected at first; the car page shows its id. Enter that id in the setting "Ladepunkt-ID der Wallbox". The Halo retries by itself and is then accepted.

## Stage 2 implementation (BMW CarData)

- **Sign-in:** OAuth device-code flow with PKCE (S256) at `customer.bmwgroup.com/gcdm/oauth/device/code` and `/token`, scope `authenticate_user openid cardata:api:read cardata:streaming:read`, client id from the BMW customer portal (no secret). Started by the admin on the car page, which shows the user code and the BMW link; the controller polls in the background (honours `authorization_pending` and `slow_down`).
- **Tokens:** only the refresh token and the account id (gcid) are stored, encrypted with the existing data-protection key ring (`bmw.cardata.session.*`, not writable through the settings API). The ID token (one hour) stays in memory and is renewed five minutes before expiry; a refresh rejected with 400/401/403 clears the sign-in and asks for a new one. Error messages carry only the OAuth error code and description, never token values.
- **Stream:** MQTT 3.1.1 over TLS 1.3 to `customer.streaming-cardata.bmwgroup.com:9000`, client id and user name = gcid, password = ID token, topic `<gcid>/<VIN>` (or `<gcid>/+` with VIN `auto`). Reconnects with backoff 30 s to 10 min; BMW allows one stream per account, so no other CarData stream client may run with the same account.
- **Values:** state of charge (`...stateOfCharge.displayed`, `...batteryManagement.header` or `...charging.level`), target state of charge, charging status, cable plugged, electric range, odometer. Messages for another VIN and implausible values are ignored. The last values are persisted at most once a minute (`bmw.cardata.session.lastSnapshot`) so they survive a restart.
- **Car page:** connection state, sign-in code, values with timestamps, setup steps and the descriptors to select (admin only).

### Setup

1. BMW customer portal (as primary user of the car) → BMW CarData: create a client, subscribe it to CarData API and CarData Streaming, select the descriptors listed on the car page in the stream configuration.
2. Settings → Verbindungen → BMW CarData: enter the client id (VIN optional).
3. Car page → "Bei BMW anmelden", open the BMW link, confirm the code.

## Stage 3 implementation (manual trip planning)

Operator requirements (2026-11-01): enter "on date X I drive to Y"; adjustable minimum state of charge; the distance is always calculated automatically, outbound and return, with a 5 % margin; weekly series on chosen weekdays (e.g. every Tuesday and Thursday); show the calculated course, i.e. when which state of charge must be reached.

- **Trips:** destination (free text), departure, optional return; optionally weekdays and an end date for a weekly series. Home is the installation location from the PV forecast settings. Distance and duration come from OpenStreetMap (Nominatim geocoding, public OSRM routing; identifying User-Agent, two requests per entry) and are stored with the trip; they are not edited by hand. A series keeps the local departure time of the first trip across daylight-saving changes and is expanded 14 days ahead; deleting removes the whole series.
- **Energy per trip:** 2 x one-way distance x consumption x (1 + margin). Settings (section Ladestrategie, "Auto & Fahrten"): usable capacity 81 kWh (i4 eDrive40/M50), consumption 18 kWh/100 km, minimum state of charge 15 %, margin 5 %, wallbox power 11 kW.
- **Required state of charge:** at each departure the trip energy plus what must remain afterwards: at least the minimum state of charge, and more if the next trip cannot be charged in the time at home in between (backward pass, charging at wallbox power x 0.9 assumed AC losses). Without an entered return the car is assumed back at 20:00 local time, but not before twice the driving time.
- **Course:** the minimal course from the current state of charge (CarData; minimum state of charge if unknown): charged only as much as needed and as late as possible. Shown as a chart and as a table with the latest charge start and the expected state of charge on return. Trips that need more than a full battery or cannot be reached in time are flagged.
- **API:** `GET/POST /api/ev-charging/trips`, `DELETE /api/ev-charging/trips/{id}`; changes are admin-only (central write guard).

## Stage 5 implementation (calendar)

Operator decisions (2026-11-01): calendar before charging control; only appointments with a location, or a place evident from the subject; the Entra app of the sign-in already has the permission (application permission Calendars.Read); calendar trips are taken over automatically and can be hidden one by one; places from the subject only from unambiguous patterns.

- **Access:** Microsoft Graph with client credentials of the existing Entra app (tenant, client id and secret from the environment file), `GET /users/{mailbox}/calendarView` for the next 14 days in UTC, only id, subject, location, start, end, all-day, cancelled and online-meeting flags. Settings: `ev.calendar.enabled`, `ev.calendar.mailbox`. Sync every 15 minutes and on demand from the car page.
- **Place:** the location field (display name, otherwise street, postcode and city); online meetings (Teams, Zoom, links, phone) are never trips. Without a location only subject patterns count: "... in Hamburg", "nach Frankfurt am Main", "Kunde X, Leipzig", "@ Köln", excluding common non-places ("nach Absprache", "in Ruhe"). A place from the subject must be known to OpenStreetMap as a settlement (`featureType=settlement`). Places closer than 1 km are not trips. Places that are not found are listed on the car page.
- **Trip:** departure = start minus driving time, return = end plus driving time; all-day appointments count as on site from 08:00 to 18:00 local time on their first and last day. The route is requested again only when the place changes (one request per place and sync, at most one per second). Cancelled, deleted or past appointments remove their trip; a hidden ("ignored") trip stays hidden. Deleting a calendar trip on the car page hides it.
- **Privacy:** appointment subjects are stored with the trip and shown only to admins; viewers see the destination only. The Graph token stays in memory; error messages carry no secrets.
- **Security note:** the application permission Calendars.Read allows reading every mailbox in the tenant. Restrict the app to the one mailbox with Exchange Online RBAC for Applications (or an application access policy).

## Open points

- Halo firmware version and whether it accepts `TxDefaultProfile` changes during a transaction (stage 4).
- Exact i4 variant and typical consumption: defaults 81 kWh and 18 kWh/100 km until the operator adjusts them.
- Winter surcharge from the temperature forecast (rule 2) is not applied yet; the margin covers it for now.
- Skipping a single occurrence of a series (holiday) is not possible yet.
- Exchange restriction of the calendar permission to the one mailbox (operator task).
- Exact CarData charging status values and whether the car reports plugged state as boolean; to be checked with real messages.
