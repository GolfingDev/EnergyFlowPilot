# EV Charging (Charge Amps Halo, BMW i4)

Status: requirements and staged plan agreed with the operator on 2026-10-31. Stages 1 (OCPP, read-only), 2 (BMW CarData, read-only) 3 (manual trip planning) and 5 (calendar) implemented; stage 4 is split into 4a charging plan (implemented, preview), 4b wallbox command with hardware test (implemented), 4c automatic control (implemented, default off) and 4d joint battery integration (implemented).

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

- **Trips:** destination (free text), departure, optional return; optionally weekdays and an end date for a weekly series. Home is the installation location from the PV forecast settings. Distance and duration come from OpenStreetMap (Nominatim geocoding, public OSRM routing; identifying User-Agent, two requests per entry) and are stored with the trip; they are not edited by hand. A series keeps the local departure time of the first trip across daylight-saving changes and is expanded 14 days ahead; deleting removes the whole series, while single dates can be skipped (stored as local dates on the series, `PUT /api/ev-charging/trips/{id}/occurrences/{date}/skipped`) and brought back from the list of hidden trips.
- **Energy per trip:** 2 x one-way distance x consumption x (1 + margin) x (1 + winter surcharge). Winter surcharge: `ev.trip.winterSurchargePerKelvinPercent` (default 1.2 %) per kelvin below 15 °C, from the mean forecast temperature between departure and return (Open-Meteo, already used for the consumption model), capped at 40 %; without a forecast no surcharge and a hint on the car page. Settings (section Ladestrategie, "Auto & Fahrten"): usable capacity 81 kWh (i4 eDrive40/M50), consumption 18 kWh/100 km, minimum state of charge 15 %, margin 5 %, wallbox power 11 kW.
- **Required state of charge:** a later trip that needs more than a full battery is not carried backwards (it has to charge on the way anyway); the earlier trip then says it only works with charging before that trip.
- **Required state of charge (rule):** at each departure the trip energy plus what must remain afterwards: at least the minimum state of charge, and more if the next trip cannot be charged in the time at home in between (backward pass, charging at wallbox power x 0.9 assumed AC losses). Without an entered return the car is assumed back at 20:00 local time, but not before twice the driving time.
- **Course:** the minimal course from the current state of charge (CarData; minimum state of charge if unknown): charged only as much as needed and as late as possible. Shown as a chart and as a table with the latest charge start and the expected state of charge on return. Trips that need more than a full battery or cannot be reached in time are flagged.
- **API:** `GET/POST /api/ev-charging/trips`, `DELETE /api/ev-charging/trips/{id}`; changes are admin-only (central write guard).

## Stage 4a implementation (charging plan, preview only)

- **Slots:** quarter hours of the current joint battery plan (price, PV and consumption forecast, planned home battery charging). The PV surplus offered to the car is what remains after house consumption and planned home battery charging, i.e. what would be fed in; the car never takes PV the home battery plan counts on. Without a joint plan younger than 30 minutes the plain price forecast is used (no PV surplus).
- **Order:** (1) for each departure, earliest first, the missing energy goes into the cheapest quarter hours before it; PV surplus counts at the feed-in price, grid at its price. Energy for a departure beyond the price horizon is partly deferred (half of what full power could charge after the horizon). (2) Remaining PV surplus is always used, up to the charge limit set in the car (CarData), and only where the surplus alone reaches the wallbox minimum. (3) The cheapest remaining quarter hours fill up to the base state of charge (`ev.car.baseSocPercent`, default 50 %).
- **Constraints:** no charging between departure and return; at least 6 A x 3 phases (4.14 kW) in every planned quarter hour, at most the wallbox power; 90 % assumed charging efficiency. Missing energy and an unreachable base state of charge are reported.
- **Car page:** "Ladeplan" card with charging blocks (time, power, grid and PV energy, average price, reason) and the expected grid cost. `GET /api/ev-charging/charge-plan`.

## Car charging optimizer (replaces the rule order of stage 4a)

Operator decisions (2026-11-01): PV goes where it saves more (car or home battery); without a trip the car charges when it pays off (replaces the "cheapest 25 %" rule); the learned PV correction is shown first and applied only by switch.

- **Why not one joint optimization:** the home battery planner already evaluates about 2,000 state combinations per quarter hour; the car as a third dimension would multiply that by about a hundred, too much for the Raspberry Pi. The two plans are coupled by prices instead.
- **Home battery value:** the joint battery planner reports, per quarter hour and battery, what one more stored kWh saves later (`JointBatteryTarget.EnergyValuePerKwh`, the slope of the remaining cost over that battery's energy). The plan itself is unchanged.
- **Car optimizer:** dynamic programming over the car's battery energy (1 kWh steps up to its charge limit) per quarter hour; actions 0 or 6 to 16 A on three phases (and an exact fill-up to the limit). Each wallbox kWh comes from the cheapest source of its slot, sorted by price: PV that would be fed in (feed-in price), PV the home batteries would store (their marginal value x 0.95 for charging losses), grid (price, +0.02 EUR/kWh on estimated prices).
- **Requirements:** at each departure the car must hold the trip energy plus the minimum state of charge (penalty 10 EUR per missing kWh, so it is always met when physically possible); the trip energy leaves at departure; no charging while away. Departures after the horizon: what cannot wait for later prices (half of full power until then may) must be in the battery at the horizon end.
- **Economic charging:** energy left at the horizon end is worth 85 % of the median slot price (after charging losses), so the car fills up in clearly cheap hours, up to its own charge limit. The base state of charge is a soft lower bound (1 EUR per missing kWh) to be reached within 24 hours: what full power could charge between the end of the published prices and that deadline may wait (half of it), so a cheaper night tomorrow can still be used.
- **Unknown state of charge:** without CarData only trips are planned, cautiously from the minimum state of charge; no base or economic filling, since the car might already be full (operator 2026-11-01).
- **Scenarios:** for departures the plan relies on only 70 % of the PV forecast; more sun is used live by the control. The car page shows the plan's grid energy and cost for little (50 %), expected and much (130 %) PV.
- **Coupling:** the joint plan contains the car's planned load (stage 4d); the car plan adds it back when splitting the PV of a slot. Both are recalculated every few minutes and converge.

## Site address, address suggestions and permissions (2026-11-01)

- **One site address:** batteries, PV and wallbox are always at the same place. The settings card "Standort der Anlage" (Stromtarif & Prognosen) takes the address from a suggestion and stores it (`site.address`, not editable through the generic settings API) together with the PV forecast coordinates; trips start there. Changing it recalculates the distance and driving time of all stored trips in the background (one route per second).
- **Address suggestions:** Nominatim forbids search-as-you-type, so suggestions come from Photon (photon.komoot.io, OpenStreetMap data, results near home first) through the controller (`GET /api/places`, at least three characters, 350 ms typing pause in the browser, 10 minutes server cache). A chosen suggestion is routed directly with its coordinates; free text is still geocoded with Nominatim.
- **Permissions:** changes are admin-only for everything (central write guard). Reading trips (destinations, times), the calendar status, the site address and address suggestions is admin-only as well (`RequireAdmin` endpoint filter; open without sign-in). Viewers see the car page with the charging plan, the car state and the wallbox, but not the trip card; destinations in charging plan hints are replaced by "zu einer Fahrt".

## Learned parameters

- **Consumption per km:** every CarData message that changes the state of charge or the odometer is stored (`CarSamples`, 400 days). Reports belong to one drive until the car charges (state of charge rises) or nothing comes for two hours (parked, so standby losses do not count). Drives of 10 to 1,000 km give (state-of-charge drop x usable capacity) / km, normalised to mild weather by dividing out the winter surcharge for the past temperature; implausible values (outside 8 to 50 kWh/100 km) are dropped. The median of the last 20 drives is used once at least three drives with 60 km together exist and `ev.car.useLearnedConsumption` is on (default); the trip planner then adds the winter surcharge for the forecast temperature. The car page says whether the setting or the learned value is used.
- **PV forecast correction:** the joint planning stores the latest PV forecast per quarter hour before it starts (`PvForecastSnapshots`, at most every 15 minutes, 30 days). Hourly, measured PV (E3/DC PV meter, quarter hours with at least three samples) is compared with these forecasts over 14 days, per local hour of day and overall. Factors are pulled towards 1 with fewer than seven days of data and stay within 50 to 150 %. The settings page (Stromtarif & Prognosen) shows them; only with `pvForecast.learnedCorrectionEnabled` (default off, operator decision 2026-11-01) and at least seven days are the PV forecasts of the joint battery plan (and thus of the car plan) scaled.

## Stage 4b implementation (wallbox command, hardware test)

- `IEvChargerControl` (implemented by the OCPP status store for the connected wallbox): `SetChargingProfile` with a TxDefaultProfile (id 70001, stack level 1, unit A, three phases) that is valid for at most one hour (`validTo`, `duration`), tried on connector 1 and then connector 0; `ClearChargingProfile` for that id ("Unknown" counts as released). 0 A pauses, otherwise 6 to 16 A.
- **Fail-safe:** every limit expires by itself, so the wallbox returns to its own setting (full power) when the controller stops sending; this keeps rule 1 even if the controller fails. Whether the Halo honours `validTo` must be confirmed in the hardware test.
- **Hardware test (admin, car page, wallbox card):** pause, 6, 10 or 16 A for 15 minutes, and "Freigeben"; the wallbox's answer and the following phase currents show whether the limit works.

## Stage 4c implementation (automatic control)

Operator requirement (2026-11-01): charge beyond the trip plan whenever power is cheap or PV is plentiful and the home batteries are full; limit the current so that little comes from the grid.

- **Switch:** `ev.charging.controlEnabled`, default off; switch on only after the hardware test.
- **Cycle (30 s):** charging plan refreshed every 5 minutes and at each new quarter hour. With the car plugged in: current of the planned quarter hour (grid part) or live PV surplus, whichever is more; otherwise pause (0 A). Without a usable plan (no prices, error) no limit at all: the wallbox charges at full power (rule 1).
- **PV surplus live:** grid export plus the car's own power, averaged over six samples (3 minutes) so passing clouds do not toggle charging; 300 W kept free; current = floor(surplus / 690 W), 6 to 16 A. Just below the minimum, 6 A are used if at most `ev.charging.pvGridAllowanceWatts` (default 1000 W) come from the grid. The home batteries absorb PV first (joint limiter); the car gets what would be exported, i.e. surplus once the home batteries are full or at their limit.
- **Cheap power:** handled by the car charging optimizer (economic value of stored energy); the former percentile setting `ev.charging.cheapPercentile` is no longer used.
- **Commands:** sent only on change; renewed 5 minutes before the 15-minute validity ends; after a refusal retried after 5 minutes. Switching the automatic off releases the limit.
- **Car page:** state chip in the charging plan card (Lädt nach Plan, PV-Überschuss, Pausiert, Volle Leistung) with the reason and command problems.

## Stage 4d implementation (joint battery integration)

- **Consumption history:** every live sample stores the wallbox power (`LiveConsumptionSamples.EvChargingWatts`, from OCPP, at most two minutes old; 0 when no car is plugged in). The joint consumption model subtracts it, so car charging is no longer learned as house demand. Older samples have no value and stay as they are.
- **Joint plan:** while the automatic car charging is on, the car's planned energy per quarter hour is added to the consumption forecast of the joint battery plan. The planner then decides, as for any house load, whether the home batteries cover it (only when cheaper than grid power); otherwise the car charges from the grid and the live limiter does not discharge for it.
- **No oscillation:** the PV surplus offered to the car adds its own planned charging back to the joint plan's consumption, so the car plan does not shrink because of itself.

## CarData queries (when the stream stays silent)

- REST API `https://api-cardata.bmwgroup.com` (header `x-version: v1`, access token of the same sign-in): the controller creates one data container `EnergyFlowPilot v1` with the descriptors it evaluates (id stored in `bmw.cardata.session.containerId`) and reads `GET /customers/vehicles/{vin}/telematicData?containerId=...`. Without a configured VIN the primary vehicle comes from `GET /customers/vehicles/mappings`. The answer goes through the same parser as stream messages.
- **When:** if no car data has arrived for an hour (then at most hourly), right after a car is plugged in at the wallbox (at most every 10 minutes), and with "Jetzt abfragen" on the car page. BMW allows about 50 calls per day; the controller stops at 40 (counted per UTC day in memory) and treats HTTP 429 as quota used up.
- The car page (admin) shows the calls today, the last call with its trigger, and problems.

## CarData stream diagnostics

The car page (admin) shows the subscription result, the number of stream messages since start, the time and descriptor names (no values) of the last message, and why a message was not used. A refused subscription is an error (with the hint to enter the VIN instead of `auto`). The first message per connection is logged with its descriptor names.

## Stage 5 implementation (calendar)

Operator decisions (2026-11-01): calendar before charging control; only appointments with a location, or a place evident from the subject; the Entra app of the sign-in already has the permission (application permission Calendars.Read); calendar trips are taken over automatically and can be hidden one by one; places from the subject only from unambiguous patterns.

- **Access:** Microsoft Graph with client credentials of the existing Entra app (tenant, client id and secret from the environment file), `GET /users/{mailbox}/calendarView` for the next 14 days in UTC, only id, subject, location, start, end, all-day, cancelled and online-meeting flags. Settings: `ev.calendar.enabled`, `ev.calendar.mailbox`. Sync every 15 minutes and on demand from the car page.
- **Search near home first:** every place search (calendar and free text) looks within about 80 km around the site first (Nominatim `viewbox` with `bounded=1`) and only then nationwide, so a location like "Lidl" is the nearby store (operator report 2026-11-01: a far-away Lidl gave 2 x 264 km). Calendar routes from the older search carry `RouteVersion` 0 and are searched again once with the next sync.
- **Place:** the location field (display name, otherwise street, postcode and city); online meetings (Teams, Zoom, links, phone) are never trips. Without a location only subject patterns count: "... in Hamburg", "nach Frankfurt am Main", "Kunde X, Leipzig", "@ Köln", excluding common non-places ("nach Absprache", "in Ruhe"). A place from the subject must be known to OpenStreetMap as a settlement (`featureType=settlement`). Places closer than 1 km are not trips. Places that are not found are listed on the car page.
- **Trip:** departure = start minus driving time, return = end plus driving time; all-day appointments count as on site from 08:00 to 18:00 local time on their first and last day. The route is requested again only when the place changes (one request per place and sync, at most one per second). Cancelled, deleted or past appointments remove their trip; a hidden ("ignored") trip stays hidden. Deleting a calendar trip on the car page hides it.
- **Privacy:** appointment subjects are stored with the trip and shown only to admins; viewers see the destination only. The Graph token stays in memory; error messages carry no secrets.
- **Security note:** the application permission Calendars.Read allows reading every mailbox in the tenant. Restrict the app to the one mailbox with Exchange Online RBAC for Applications (or an application access policy).

## Open points

- Halo firmware 189 is connected. Hardware test open: does it accept the TxDefaultProfile (connector 1 or 0) during a running charge, follow it within seconds, and fall back to full power when `validTo` passes?
- Exact i4 variant and typical consumption: defaults 81 kWh and 18 kWh/100 km until the operator adjusts them.
- Exchange restriction of the calendar permission to the one mailbox (operator task).
- Exact CarData charging status values and whether the car reports plugged state as boolean; to be checked with real messages.
