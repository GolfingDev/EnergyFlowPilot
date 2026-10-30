# Battery Decision Engine

## Purpose

The Battery Decision Engine decides how the controller should handle charging, discharging or staying idle based on price data, battery state, Victron telemetry and configuration.

## Responsibilities

The Battery Decision Engine has two main functions:

- Forecast calculation for frontend display.
- Direct decision calculation for the current point in time.

Both functions must use the same domain rules where possible, so the frontend forecast and the actual live decision remain explainable and consistent.

## Forecast Calculation

The forecast calculation prepares a time-based outlook for display in the frontend.

The forecast calculation uses 15-minute time slots.

The forecast inputs include:

- calculated consumption based on historical data
- expected PV yield
- future Tibber electricity prices
- current battery state of charge
- configured total battery capacity in kWh
- configured maximum charge and discharge power

For early Battery Decision Engine development, these inputs should be provided by deterministic manual test doubles:

- fake Tibber price forecast
- fake weather/PV forecast
- fake historical-consumption based forecast
- fake current battery state of charge

The initial consumption test profile may be modeled as an average day for a three-party residential household. This profile is test data only and must not become a hidden production default.

The forecast should contain:

- forecast time window in UTC
- forecast entries for 15-minute time slots
- expected decision type per time slot
- relevant input values per time slot
- calculated consumption per time slot
- expected PV yield per time slot
- Tibber electricity price per time slot
- initial battery state of charge used for the forecast
- structured reasons per forecast entry
- display-ready DTOs only at the API boundary

Each forecast entry must use the projected battery state of charge for its time slot once forecasted battery state calculation exists.

Forecast charging decisions are cost-optimal over the whole horizon (`BatterySocOptimizer`, dynamic programming):

- by default the battery covers expected consumption in every slot down to the planning minimum state of charge (`SELF_CONSUMPTION_DISCHARGE`)
- the optimizer models the stored battery energy on a 0.05 kWh grid between planning minimum and maximum. A backward pass computes, for every slot and energy level, the cheapest cost until the horizon end. A forward pass then follows it from the real start energy
- per slot it chooses the next battery energy within the physical limits: maximum charge and discharge power, single-direction efficiency, no discharge beyond the expected import (no feed-in from the battery), and PV surplus is always stored as far as power and headroom allow
- energy cost per slot: grid import at the slot price, export at the feed-in compensation
- deviations from the default behaviour are planned as:
  - holding battery energy (`HOLD_FOR_EXPENSIVE_SLOT`): the battery covers less than it could
  - grid charging (`PLANNED_GRID_CHARGE`): only on published, non-negative prices
- the configured minimum savings (`decision.minimumSavingsPerKwh`, default 0.03 EUR/kWh) is a hurdle, not a recurring storage fee. Grid purchases carry it once per input kWh. For existing/PV energy, the optimizer builds a fixed reference trajectory (cover consumption, store surplus, no price-based holds or grid purchases) and charges only positive increases of deferred stored energy relative to that baseline, converted to deliverable kWh. Grid-charged increments are excluded from the hold hurdle because the purchase already carries its own hurdle. An unchanged deferred stock is not charged again simply because another quarter hour passes (fix of 2026-10-21)
- grid charging additionally carries the configured battery wear cost (`battery.wearCostPerKwh`, battery price divided by usable kWh times cycles); holding causes no extra cycle and therefore no wear cost
- Rolling replanning must not apply the hold hurdle again to an already purchased grid increment. The decision log stores `GridChargeEnergyCredit` with measured SoC, capacity and supplier. Only measured positive SoC changes under a still-valid automatic grid-charge command earn credit, capped by commanded power, elapsed time and estimated PV surplus. A command without measured gain, manual charging and PV-only charging earn none. Measured discharge consumes credit first; PV gains do not increase it. Both forecast and direct control advance the same persisted snapshot without mutating it on dashboard reads. Missing/invalid history, supplier/capacity changes, backwards timestamps or sample gaps over 30 minutes reset credit conservatively. This is a planning provenance estimate, not a separate electricity meter or measured savings account.
- The legacy `battery.targetEndStateOfChargePercent` is retained unchanged for compatibility (0 to 100%), but neither controls the optimizer nor restricts the planning range. The actual limits are `hard minimum <= planning minimum < planning maximum <= 100%`.
- the optimizer sees all combinations at once. For example, holding energy early can make a smaller, cheaper charge later sufficient. The previous greedy planner closed one shortage after the other and could miss such combinations
- PV surplus slots are grid charge candidates as well: PV rarely uses the full charge power (the E3/DC takes PV first), so the charge power left next to the PV surplus can be filled from the grid when the slot is the cheapest source; the direct decision then charges the live surplus plus the planned grid share
- only the energy that pays off is charged, never a blanket fill to the planning maximum
- the result uses the same plan format as before (hold and charge energy per slot); the simulator applies it and stays the model for state of charge and explanations. Because of the energy grid, planned amounts can differ from the exact need by a few Wh
- the direct decision plans the current slot with the live load (`BatteryForecastRequest.CurrentNetLoadWatts` = grid power without the Victron battery minus what the E3/DC charges from the site) instead of the consumption forecast. Unplanned loads such as an electric car are not covered automatically: the optimizer only covers them as far as the battery energy is not worth more in a later, more expensive slot (2026-10-17: car at 0.33 EUR/kWh, evening at 0.39 to 0.44 EUR/kWh)

In External ESS mode the Multi controls its AC-in power to the published setpoint and supplies AC-out loads in addition, so the battery power equals the setpoint minus the AC-out load. On this site the whole house phase L1, including the wallbox share, is on AC-out. The setpoint is therefore the signed battery target plus the measured AC-out power per phase (`vebus/276/Ac/Out/L{n}/P`). The setpoint refresh rebuilds it every 5 seconds with the current AC-out load. Without this, an idle battery still covered L1, a discharge delivered its target plus the L1 load, and a planned grid charge could turn into a discharge while L1 drew more than the charge power.
- there is no end-of-horizon reserve (`END_SOC_RESERVE` is no longer produced): the horizon end only reflects when Tibber publishes prices, and the plan is recalculated as soon as they are known
- missing prices up to 48 h ahead are estimated from a comparable day (`ComparableDayPriceEstimator`): Tuesday to Friday use the previous day, Saturday, Sunday and Monday the same weekday one week earlier; past prices come from the decision log, the current day from Tibber; negative estimated prices are set to 0
- the comparable-day estimate is then replaced by a weather-based estimate (`WeatherPriceEstimator`) where inputs are available:
  - a linear model learns the German spot price from a wind index (turbine curve over five northern locations, Open-Meteo `wind_speed_100m`), the irradiance over five southern and central locations, 23 local hour dummies, and Saturday and Sunday
  - training data: 42 days of hourly weather and Energy-Charts day-ahead prices (`PriceDriverProvider`, three requests per hour at most, cached including failures)
  - the Tibber end price is calibrated as `intercept + slope x spot` on the published slots of the forecast
  - on the held-out week 2026-10-11 to 10-18, the error was 43 EUR/MWh instead of 66 EUR/MWh for the comparable day, and 91 % of hour pairs more than 2 ct apart were in the right order
  - without inputs, model (fewer than 168 training hours) or calibration (fewer than 16 published slots, no spread), the comparable-day estimate stays
- estimated slots (`IsPriceEstimated`) may hold energy and show shortages, but grid charging is only planned on published prices
- Comparable-day estimation matches local quarter hours. On the autumn source day the two occurrences of a repeated local quarter hour are averaged, identically for published and logged prices. A missing spring-hour source price ends the estimate rather than inventing a replacement. Target slots always advance in UTC, including both occurrences of an autumn hour.
- if multiple plans have the same cost, the default behaviour wins, so the forecast remains deterministic
- negative prices keep their own rules: the cheapest negative slots are planned for a full charge
- there is no grid charging without an expected consumption shortage; energy that PV or the battery would provide anyway must not be bought
- the projected state of charge must be updated slot by slot after every planned charge, discharge, PV surplus and consumption decision
- the projected state of charge must apply configured round-trip efficiency physically, not only as an audit metric
- the first efficiency model uses `sqrt(roundTripEfficiencyPercent / 100)` as single-direction efficiency for charging and discharging
- charging stores less energy than is taken from PV or grid when efficiency is below 100 percent
- discharging removes more battery energy than reaches the load when efficiency is below 100 percent

The first forecast simulator calculates each slot in chronological order and includes:

- state of charge before and after the slot
- expected PV yield
- expected consumption
- expected grid import before battery action
- Tibber price
- Decision Engine instruction
- target power in watts
- structured reasons

The first simulator is intentionally conservative. It uses configured capacity, minimum state of charge and maximum charge/discharge power. It does not execute commands and it does not replace the later realtime decision path.

The forecast application service must orchestrate all required inputs through interfaces:

- Tibber price forecast provider
- weather/PV forecast provider
- historical consumption forecast provider
- battery state provider
- battery configuration provider
- persisted controller settings

The service must read the configured feed-in compensation from persisted settings. It must not silently fall back to a hardcoded value when the setting is missing or invalid.

The first real PV forecast provider uses Forecast.Solar Public API. It requires no API key, but it must read site and PV system settings from persisted controller settings. Because the public response is hourly, the provider maps each hourly energy delta evenly to four 15-minute Decision Engine forecast slots. This interpolation must be visible as a provider limitation and must be replaceable later, for example by Solcast or a paid Forecast.Solar tier with finer granularity.

Roofs with several planes (e.g. east/west) are configured as comma-separated lists in `pvForecast.peakPowerKwp`, `pvForecast.declinationDegrees` and `pvForecast.azimuthDegrees`; a single value applies to all planes. The provider sends one request per plane and adds the yields. Because the direct decision requests a window that moves every 15 minutes, the cache stores one fixed three-day window per UTC day and slices the requested range from it, so the public rate limit (12 requests per hour) is never reached.

The frontend-facing forecast API must expose DTOs only. Domain models must not be returned directly to the frontend. The first API endpoint delegates to `IBatteryForecastService` and maps the result to a DTO containing time slot, inputs, decision, target power, projected state of charge and structured reasons.

The forecast is advisory for the user interface. It must not directly execute hardware commands.

## Direct Decision Calculation

The direct decision calculation produces the actionable decision for the current point in time.

The direct decision should contain:

- decision type
- target power in watts for `Charge` or `Discharge`
- valid time window in UTC
- current battery state
- current or latest valid Victron telemetry
- relevant price data
- structured reasons
- command intent, if a hardware command should later be issued
- persisted decision log entry with all structured reasons

Only the direct decision path may lead to command execution, and command execution must happen outside the Battery Decision Engine through a hardware abstraction.

MQTT-based Victron telemetry must always be consumed as live data. It must not be served from a long-lived application cache for direct decisions, because battery state, grid import and house consumption change too quickly.

Tibber price forecasts and weather or PV forecasts may be cached because they change much more slowly. A cache duration of several hours is acceptable for normal operation, and a once-per-day refresh may already be sufficient when the provider data quality and update cadence support it.

Before every direct control decision, the controller must read and validate the latest live inputs again. Forecast values may guide planning, but they must not be used as a silent replacement for current telemetry when a real hardware command could be issued.

The live pre-check must include at least:

- current battery state of charge from Victron telemetry
- current house consumption or grid import at the grid connection point
- current grid export, represented as negative grid import if applicable
- current PV production where available
- current battery charge and discharge limits from configuration
- latest valid Tibber price slot for the current UTC time
- freshness timestamps for all live telemetry values

If required live inputs are missing, stale, inconsistent or outside plausible physical limits, the direct decision must be `Idle`, must not issue a command intent and must log a structured reason that names the invalid input.

Every direct decision must be logged with its input summary, result, target power, relevant time window and structured reasons. Decision log retention must be configurable through persisted settings and frontend-editable later.

Grid feed-in from the battery is forbidden under all circumstances. For `Discharge`, the target power must be limited by the grid import the site would have without the running battery action. If that value is `0` watts or negative, the direct decision must not discharge and must explain that discharging would risk grid feed-in.

The measured grid power already contains the running battery action. Direct decisions therefore use the grid power without battery action: measured grid power minus the battery power, converted from the DC side to the AC side with the single-direction efficiency. Using the residual grid import as next target would only cover about half of the load and make charging and discharging oscillate. While discharging, the DC power also feeds loads on the inverter AC output and contains losses that grow with power; both are invisible at the grid meter and led to 350-500 W feed-in on 2026-10-14. Therefore the battery contribution is the smaller of the DC-based estimate and the previously requested discharge power, which closes the control loop on the measured grid power. Discharge keeps the grid power deadband as import margin.

A supplementary storage system (E3/DC) regulates the same grid connection and has its own price logic, including grid charging in cheap phases. Whatever it charges beyond its own PV production (`SupplementaryBatteryChargeFromSiteWatts`) is never covered by the Victron discharge. Otherwise energy moves from one battery into the other with double conversion losses, and both controllers can escalate each other: on 2026-10-14 between 21:10 and 00:10 the Victron battery charged the E3/DC battery from 57 to almost 100 percent. The same share is excluded from the learned consumption profile, because it is no house demand the Victron battery will cover.

Planned grid charging (including the grid share during PV export) and manual grid charging now pass a shared `SupplementaryGridChargeGuard` before a decision is persisted. A configured E3/DC must provide a known battery-power reading no older than three minutes; the older of source measurement and successful poll determines freshness. More than 50 W E3/DC discharge blocks Victron grid charging. Missing/stale values fail closed instead of becoming a measured zero. A detected discharge or missing-data condition starts a five-minute restart delay; every repeated conflict extends it. The shared guard survives scoped service recreation; its cooldown is process-local. After process restart the telemetry checks still apply. Pure PV absorption retains the existing subtraction of secondary-battery discharge from measured surplus.

The hardware publisher repeats this check immediately before writes. External ESS also checks each five-second refresh. A safety stop sets the signed battery target and the retained desired target to zero immediately, bypasses the normal 250 W ramp, and writes DisableCharge/DisableFeedIn even without readback. AC-out loads remain included in the external setpoint, so stopping battery charge does not request battery discharge to supply the house. Write batches from decision publication and refresh share a lock; a refresh cannot restore an obsolete charging target. A stopped target requires a new approved decision to restart. Fresh E3/DC polls trigger recalculation.

This integration monitors the independently controlled E3/DC; it does not inhibit E3/DC discharge directly. Detection is limited by cloud measurement/polling latency, so a short transfer before a newly started E3/DC discharge is observed cannot be ruled out. The dashboard flags simultaneous Victron charging and E3/DC discharge as a critical transfer risk and distinguishes requested charge power from measured grid power.


Negative grid import without battery action means the site is currently exporting energy. The Battery Decision Engine should absorb this export by charging the battery while the battery is not full. The target charge power must be limited by the absolute export without battery action and by the configured maximum charge power.

The direct decision takes grid charging and holding from the forecast plan for the current slot, so forecast chart and real control follow the same rules:

- planned grid charge: charge with the planned power, capped by settings and the planning maximum
- planned hold (`HOLD_FOR_EXPENSIVE_SLOT`, `END_SOC_RESERVE`, waiting for a negative price window or a negative current price): stay idle
- partial hold: discharge, but at most with the planned power
- otherwise: cover the live demand down to the planning minimum state of charge
- if the forecast is unavailable, the battery covers the live demand and no grid charging is started

There is one economic exception: when future Tibber prices include negative grid-charging slots, the Battery Decision Engine may preserve battery headroom instead of absorbing current export. This decision must compare the future negative-price charging value against the configured feed-in compensation price per kWh. The configured feed-in compensation must be persisted and frontend-editable later.

## Explainability

Every forecast entry and every direct decision must be explainable.

A decision must contain:

- decision type
- valid time window in UTC
- relevant input values
- structured reasons
- rule names
- result summary

## Savings Accounting

The system should account how much money the battery strategy saved.

The first accounting level stores daily values. Week, month, year and total views are derived by aggregating those daily values.

Daily savings accounting must include:

- energy charged from the grid
- cost of grid charging based on the Tibber price of the charging slot
- energy charged from PV surplus
- opportunity cost of PV surplus charging based on the configured feed-in compensation or PV sale price per kWh
- discharged energy
- avoided grid cost based on the Tibber price of the discharging slot
- net savings as avoided grid cost minus grid charging cost minus PV opportunity cost
- weighted average grid charge price, PV opportunity price and discharge price where energy exists

The configured `gridFeedIn.compensationPricePerKwh` setting is the first PV sale price input for PV surplus accounting. If a later tariff model needs a separate PV sale price, it must be added as a persisted setting with a default value.

Savings accounting is a reporting function. It must not change the actual Decision Engine action for a slot.

## Safety Rules

- Do not silently execute risky actions when required data is missing.
- Respect minimum battery state of charge once configured.
- Prefer conservative behavior when external data is stale or unavailable.
- Missing data, stale data and fallback behavior must be logged.

## Decision Output States

The Battery Decision Engine produces one of three operating states:

- `Charge`
- `Discharge`
- `Idle`

When the state is `Charge`, the Battery Decision Engine must also identify the charge source:

- `Grid`
- `PV`

`Discharge` and `Idle` do not have a charge source.

For the current direct decision, `Charge` and `Discharge` must include a positive target power in watts. The power value is an absolute value; the direction is defined by the decision state. `Idle` must use `0` watts.

Safety behavior such as missing data or stale data should be represented as `Idle` with clear structured reasons. It must not become a silent fallback.

## Tibber Price Strategy

The Battery Decision Engine must use future Tibber electricity prices to lower the average effective electricity price as much as possible.

The basic strategy is:

- cover consumption from the battery by default
- hold battery energy or charge from the grid only when a later, clearly more expensive slot would otherwise run out of battery energy (see need-based forecast charging above)
- always use negative Tibber price phases for grid charging when the battery is not full

The earlier classification into cheap, neutral and expensive thirds (`TibberPriceDecisionRule`) is no longer used for control decisions. It charged in every "cheap" third regardless of expected PV yield and consumption and therefore bought energy that PV would have provided anyway.

The current or forecasted battery state of charge must influence the price strategy:

- an empty battery cannot be discharged
- a full battery cannot be charged
- a negative Tibber price overrides normal state-of-charge restrictiveness, except when the battery is already full
- the total usable battery capacity in kWh must come from persisted controller configuration
- the configurable target end state of charge is no longer used by the planner; the planning minimum protects the battery
- the optional planning minimum state of charge is a softer forecast boundary above the absolute battery protection limit
- if no planning minimum state of charge is configured, the absolute minimum state of charge is used as fallback
- the optional planning maximum state of charge preserves headroom for possible PV forecast errors during grid charging
- if no planning maximum state of charge is configured, the physical full battery limit is used as fallback
- grid charging may exceed the planning maximum only when the Tibber price is below the configured feed-in compensation, because charging is then more valuable than preserving PV headroom
- PV surplus charging is not capped by the planning maximum and may still fill the battery up to the physical maximum
- the maximum charge power must come from persisted controller configuration and limits how quickly cheap or negative price windows can fill the battery
- the maximum discharge power must come from persisted controller configuration and limits how quickly expensive price windows can offset grid consumption
- discharge planning must never exceed current or forecasted grid import, because battery feed-in into the grid is forbidden
- negative grid import should lead to charging from surplus while the battery is not full
- preserving headroom for future negative-price grid charging is allowed only when the negative-price value is higher than the configured feed-in compensation
- before a future negative Tibber price window, the Battery Decision Engine may discharge energy above minimum state of charge to avoid normal or expensive grid import and create charging headroom
- if a negative Tibber price slot is reached and the battery still has capacity, the Battery Decision Engine must charge from the grid unless a later negative slot is strictly cheaper
- Golden Scenario audits must report final state of charge and verify it remains at or above the planning minimum state of charge
- Golden Scenario audits should use a planning reserve above the hard minimum so the forecast does not optimize exactly to the hardware protection boundary
- the state of charge influences grid charging only through the need-based plan: a full battery has no shortage to close, an almost empty battery reaches a shortage earlier
- every state-of-charge override must be included in the structured reasons

The total battery capacity must not be hardcoded. It must be configurable through the frontend in a future step, transported through DTOs at the API boundary and persisted in the database.

## PV Surplus Priority

PV surplus has priority over the Tibber price strategy for both forecast entries and the current direct decision.

For a given 15-minute slot:

- if expected PV yield is greater than expected consumption, the Battery Decision Engine must choose `Charge` with charge source `PV`
- this rule applies even when Tibber prices would otherwise suggest `Discharge` or `Idle`
- if the battery is already full, the Battery Decision Engine must choose `Idle` with a clear reason because the battery cannot be charged further
- every PV surplus override must be included in the structured reasons

The need-based planner includes battery capacity, planning minimum and maximum, maximum charge/discharge power, efficiency losses, expected consumption and expected PV yield. Its quality depends on the consumption forecast: the house consumption must include all phases, otherwise loads like a three-phase heat pump are missing and the plan underestimates the need.

The consumption profile learns from the gross site demand `grid power - Victron battery power + supplementary (E3/DC) PV production`. Victron's own AC consumption is not suitable for this site because it does not know the PV and battery behind the E3/DC system and clips each phase at 0 W. Grid power alone is not suitable either, because it already contains the Victron battery action and the profile would learn that covered load does not exist. Adding the E3/DC PV back lets the forecast subtract the PV forecast exactly once; the E3/DC battery effect stays in the profile because that energy is not available to the Victron battery. If no fresh E3/DC value is available, the summed Victron consumption topics (comma-separated list, default L1 to L3) are used as fallback.

The E3/DC PV production is the internal PV plus the external inverter ("Ext. PV" in the app). The Hager Energy API leaves `externalProduction` at 0 and reports the external inverter as a production meter (`meters[]`, `meterType = production`, negative power). Until 2026-10-19 only the internal PV was read: on 2026-10-19 that was 664 W of 4200 W. As a result, the learned daytime consumption, the check whether the E3/DC charges from the site, and the live house consumption were too low. Stored samples cannot be corrected; the profile adapts within about a week because of its recency weighting.

The forecast shape (`ConsumptionForecastModel`) uses every day of the last 14 days, not only the same weekday. A weekday profile has just two samples per slot in two weeks and reacts slowly to weather changes. Each day is weighted by recency with a half-life of three days. The model is rebuilt at most once per hour.

The heat pump makes consumption depend on the outside temperature. Hourly temperatures for the PV site (latitude and longitude from the PV forecast settings) come from Open-Meteo: 16 past days and 4 forecast days, without an API key, cached for one hour. Per local day the model calculates heating degrees, the mean of `max(0, 15 °C - T)`. It then regresses the measured daily energy of complete days against them. The fit needs at least 5 complete days and a correlation of at least 0.3. If the fit succeeds, each forecast day is scaled by `(base + slope × heating degrees of the day) / (base + slope × weighted heating degrees of the learned days)`, limited to 0.5 to 2.0. Without temperatures or without a clear dependency, the scale stays 1. A failing temperature request never breaks the plan.

Raw samples are kept for 14 days only, and two autumn weeks barely differ in temperature. Every complete day is therefore also stored as a daily summary (`ConsumptionDailySummaries`: local date, energy, heating degrees, slot count) and kept for two years. The regression uses all stored days plus the recent days, so after one heating season it knows the demand at frost as well as the summer base load. The daily shape still comes from the last 14 days. Days before 2026-10-20 are not stored, because until 2026-10-19 the external E3/DC PV inverter was missing and the learned consumption was too low by its yield.

## Interfaces

The engine should depend on abstractions, for example:

- `IBatteryDecisionEngine`
- `IBatteryForecastService`
- `ITibberPriceClient`
- `IBatteryStateProvider`
- `IBatteryConfigurationProvider`
- `IVictronTelemetryReader`
- `IVictronCommandWriter`
- `IClock`
- `IDecisionLogRepository`
- `IControllerConfigurationProvider`


### Planungshorizont und Preisqualität

Dashboard, Forecast-Standardabfrage und automatische Entscheidung verwenden einen Horizont von bis zu 48 Stunden. Dadurch kann eine günstige Nacht auch Bedarf am folgenden Nachmittag und Abend decken. Fehlen veröffentlichte Preise, ergänzen die bestehenden Schätzer den Zeitraum, soweit Vergleichsdaten verfügbar sind; fehlen auch diese, darf der sichtbare Plan kürzer bleiben. Die tatsächlichen Zeitgrenzen stehen am Plan bzw. in den Slots.

Geschätzte Preise werden in allen Dashboard-Varianten sichtbar von veröffentlichten Preisen unterschieden, inklusive Preisstatus im Tooltip. Ein Hinweis nennt den Beginn der Schätzung und erklärt, dass für diese Slots keine Netzladung geplant wird. Die Sperre gilt weiterhin sowohl im Forecast als auch für die daraus abgeleitete automatische Entscheidung. Sobald veröffentlichte Preise verfügbar sind, wird neu geplant. Ein längerer Horizont erzwingt keine Vollladung: Preisvorteil, Wirkungsgrad, Ladeleistung, PV und Planungsgrenzen bleiben maßgeblich.

Der E3/DC-Konfliktschutz bleibt unabhängig vom wirtschaftlichen Plan aktiv. Eine wirtschaftlich sinnvolle Netzladung ist keine Freigabe, Energie aus dem Zweitspeicher zu beziehen.

### Gemeinsame Steuerung von Victron und E3/DC

Implementiert ist eine gemeinsame wirtschaftliche Planung mit zwei getrennten Energiezustaenden sowie eine standardmaessig deaktivierte gemeinsame Ausfuehrung. Dynamische Programmierung mit interpolierten Folgekosten bildet getrennte Reserven, Wirkungsgrade und Leistungsgrenzen ab; sie ist eine numerische Naeherung. Shadow-Modus bleibt rein lesend. Ohne gemeinsame Ausfuehrung gilt die bisherige Victron-Regelung; bei aktivierter Ausfuehrung oder ausstehender Wiederherstellung ist sie als Hardware-Schreiber gesperrt. Die reale Hardwareabnahme und Inbetriebnahme stehen noch aus.

Betreiberwunsch vom 23.10.2026: volle verfuegbare Geraeteleistung bei Bedarf, ohne zusaetzliche pauschale Gesamtleistungsgrenze (`jointBattery.maximumGridImportWatts=0`). Bestaetigte Anschlussgrenzen je Phase und fuer den Victron-Eingang bleiben Voraussetzung fuer reale Steuerung. Die Ausfuehrung begrenzt jeden Plan an frischen Phasenmessungen, AC-out-Last, Hausbedarf und aktuellen Einzelakku-Grenzen. Unbekannte elektrische Grenzen werden nicht als unbegrenzt interpretiert.

- E3/DC-Kapazität, Notstromreserve und Leistungsgrenzen werden aus bestätigten aktuellen Gerätewerten übernommen. Die detaillierte Abfrage ergibt rechnerisch 14,014 kWh nutzbare Energie einschließlich 1,513 kWh Notstromreserve, also rund 12,501 kWh oberhalb der Reserve (siehe `docs/30-operations/e3dc-rscp.md`). Die frühere Betreiberangabe von 12 kWh ist keine feste Planungsvorgabe. Die Reserve bleibt erhalten und darf bei der Modellierung nicht doppelt abgezogen werden. Eine zusätzliche Planungsreserve darf die vorhandene Notstromreserve nicht unterschreiten.
- Jeder Speicher behält eigene Ladestände, Reserven, Wirkungsgrade und Leistungsgrenzen. Ein gemeinsamer Prozent-Ladestand darf einzelne leere oder volle Akkus nicht verdecken.
- Eine zentrale Planung verteilt Lade- und Entladeleistung auf beide Speicher. Die verfügbare Netzanschlussleistung muss für Hausverbrauch und die Summe beider Ladeleistungen ausreichen; zwei unabhängig berechnete volle Ladeziele sind unzulässig.
- Netzladung wird nur bei wirtschaftlichem Vorteil unter Berücksichtigung von Verlusten, Verschleiß, PV-Prognose und veröffentlichten Preisen geplant. Ein voller Akku muss die geplante Ladung des anderen nicht verhindern.
- Beim Laden darf der andere Speicher nicht entladen. Beim Entladen darf der andere Speicher die freigegebene Energie nicht als Ladeüberschuss aufnehmen. Zustandswechsel müssen deshalb für beide Geräte koordiniert und bestätigt werden.
- Die Summe der Entladeleistungen darf nur den gemessenen Hausbedarf decken; Entladung zum Netzexport wird nicht freigegeben. Reserven sind je Speicher einzuhalten.
- Die Verbrauchsermittlung muss nach der Umstellung die Leistung beider gesteuerter Speicher berücksichtigen. Der bisherige Ansatz für einen unabhängig betriebenen E3/DC darf nicht unverändert in eine gemeinsame Planung übernommen werden.
- Steuerung benötigt frische lokale Messwerte, bestätigte Befehle, begrenzte Gültigkeit der Sollwerte und geprüftes Verhalten bei Verbindungsabbruch, Prozessabbruch und Neustart. Ein erfolgreicher Lesezugriff belegt keine Schreibfähigkeit.
- Die wetterbasierte E3/DC-Regelung darf nicht parallel gegen dieselben Sollwerte regeln. Vor Änderungen müssen die ursprünglichen Einstellungen gesichert und deren Wiederherstellung geprüft werden.
- Forecast, Live-Entscheidung, Dashboard und Entscheidungsprotokoll müssen die geplanten und tatsächlich bestätigten Aktionen je Speicher ausweisen. Im Shadow-Modus dürfen beide Geräte nur gelesen werden.


Ausfuehrung: zwei neutrale Messungen vor Richtungswechsel, E3/DC-Echo und Modusruecklesung vor Victron-Freigabe, begrenzter Leistungsanstieg und kurze Sollwertgueltigkeit. Ruecknahme zuerst Victron, danach E3/DC neutral und erst nach Messbestaetigung Automatik. Eine persistierte Wiederherstellungspflicht sperrt konkurrierende Schreiber auch nach Neustart. Sie entsteht erst, wenn alle Startbedingungen erfuellt sind, und kann nach Pruefung vor Ort manuell bestaetigt werden, falls ein Geraet dauerhaft nicht erreichbar ist. E3/DC bestaetigt Netz- und PV-Ladung mit `EMS_MODE` 2 und Entladung mit 1. Linux-Watchdog und separater Stop-Helfer sind erforderlich; vollstaendiger Netzwerkverlust bleibt physisch zu pruefen. Details und Konfiguration: `docs/30-operations/e3dc-rscp.md`.


### Wirtschaftlicher Wert der Restenergie nach dem Preishorizont

Die gemeinsame Zwei-Akku-Planung bewertet verbleibende, oberhalb der jeweiligen Reserve tatsächlich abgebbare Energie am Ende der veröffentlichten Preise. Diese Bewertung ist Teil derselben dynamischen Optimierung; es gibt keine zusätzliche unabhängige Regel „billiger Preis = voll laden“ und keine zweite Anrechnung desselben Vorteils.

- Grundlage sind die letzten 14 abgeschlossenen lokalen Kalendertage des ausgewählten Preisanbieters und derselben Währung, aus den Entscheidungslogs. Je UTC-Viertelstunde wird ein Preis aggregiert, damit häufigere Regelläufe keinen Tag stärker gewichten. Alte unmarkierte Einzelakku-Logs gehören ausschließlich zu Tibber; alte gemeinsame Logs ohne Anbieterkennung werden nicht verwendet. Geschätzte Preise werden ausgeschlossen. Gemeinsame Logs speichern künftig Anbieter und Preisstatus ausdrücklich.
- Ein Tag zählt ab 90 % abgedeckten Viertelstunden. Die Tageslänge folgt der eingestellten Zeitzone einschließlich 23-/25-Stunden-Tagen. Mindestens drei geeignete Tage sind erforderlich. Der Median der Tagesmittelwerte unterdrückt einzelne außergewöhnlich teure Tage.
- Der spätere Bezugspreis wird um 15 % Unsicherheit vermindert und bei null begrenzt. Der Wert bezieht sich auf nach Entladeverlusten abgegebene kWh. Ladeverluste, Verschleiß und Mindestersparnis bleiben in den gewöhnlichen Kaufentscheidungen enthalten. Die Parameter sind zunächst feste, dokumentierte Modellannahmen.
- Die bewertbare Energiemenge ist für beide Akkus gemeinsam auf den positiven Nettoenergiebedarf der nächsten 24 Stunden **nach** dem veröffentlichten Preishorizont begrenzt: temperaturabhängiger Verbrauch abzüglich prognostizierter PV. Das erhält PV-Spielraum und bildet Heizbedarf ohne starre Monatsregel ab. Fehlt ein gelerntes Verbrauchsprofil, gilt der vorhandene konfigurierte Tagesverbrauch als Ersatz. Die 24 Stunden sind verstrichene Stunden in UTC, keine zwangsläufig 24 Stunden langen lokalen Kalendertage.
- Fehlen belastbare historische Preise oder eine vollständige PV-Folgeprognose, entfällt ausschließlich diese zusätzliche Bevorratung; der bisherige veröffentlichte Plan bleibt verfügbar. Forecast.Solar markiert Nullwerte außerhalb seiner tatsächlichen Prognosetage als nicht verfügbar. Solche Platzhalter dürfen keine vermeintlich PV-freie Zukunft und damit zusätzliche Netzladung begründen.
- Abgeschlossene historische Tage werden im Prozess zwischengespeichert, getrennt nach Zeitraum, Anbieter und Währung. Ein zusätzlicher Ausfall der Historie/PV blockiert nicht die bestehende Regelung. Alle langsamen Abfragen erfolgen weiterhin vor der frischen Messung beider Akkus.

Planwarnungen nennen die verwendeten historischen Tage, den typischen Preis, den verminderten Restenergiewert und die begrenzende Energiemenge. Dieser Wert ist eine Annahme über spätere vermiedene Bezugskosten und keine bereits erzielte Ersparnis. Ein voller Speicher wird nur angestrebt, wenn Preisvorteil, zukünftiger Nettobedarf und bestehende Ladegrenzen das tragen. Die tatsächliche Ausführung unterliegt weiterhin Geräte-, Anschluss- und Konfliktschutz.

### PV-Annahme innerhalb einer laufenden Viertelstunde korrigieren

Die Festlegung einer Viertelstunde darf einen weggefallenen PV-Überschuss nicht bis zum nächsten Slot fortschreiben. Zeigt der höchstens 75 Sekunden alte, mit dem aktuellen Nettobedarf korrigierte gemeinsame Plan für denselben Slot mehr als 200 W Nettobedarf und eine wirtschaftlich geplante Entladung von mehr als 200 W, ersetzt diese die bisherige Ruhe-/PV-Ladeentscheidung. Voraussetzung ist, dass die festgelegte Entscheidung noch von PV-Erzeugung mindestens in Höhe des Hausbedarfs ausging. Eine festgelegte Netzladung und manuelle Ladungen werden dadurch nicht abgebrochen. Ein fehlender, veralteter oder zukünftiger Plan löst keinen Wechsel aus; es gibt keinen pauschalen Preis-Schwellwert.

Der Wechsel erfolgt nach Vorliegen des nächsten aktualisierten Plans (regulär alle 30 Sekunden), nicht erst zur nächsten Viertelstunde. Richtungswechsel laufen weiterhin über die neutralen Bestätigungen, die Rampe und alle Live-Grenzen einschließlich Reserve und Einspeiseschutz. Das Ereignis wird protokolliert; die angezeigte Begründung nennt den korrigierten PV-Mangel statt der alten Überschussannahme. Eine wirtschaftlich begründete Zurückhaltung ohne vorherige PV-Überschussannahme bleibt unverändert.

### Grundaktion sperren, Leistungswerte nachführen

Die Viertelstunden-Sperre hält die Grundaktion (Laden, Entladen oder Halten) fest, nicht die Wattwerte. Ein frischer gemeinsamer Plan für denselben laufenden Slot aktualisiert innerhalb dieser Aktion die Gesamtleistung und die Aufteilung auf beide Speicher. Dadurch kann ein zuvor ruhender Victron eine laufende gemeinsame Entladung unterstützen; auch Reduzierungen und Änderungen beider Ladeanteile sind zulässig. Die angezeigte Begründung wird ebenfalls aktualisiert.

Eine neue Planentscheidung mit entgegengesetzter Richtung oder Halten beendet die laufende Lade-/Entladeaktion nicht. Auch die Freigabe der Ladequelle bleibt bestehen: PV-Laden wird nicht unbemerkt zu Netzladen. Fehlende, zukünftige oder mehr als 75 Sekunden alte Pläne sowie andere Zeitabschnitte ändern die letzten Sollwerte nicht. Reserve, Messwertfrische, Anschlussgrenzen, neutrale Richtungsübergänge, Rampe und Einspeiseschutz bleiben bei jeder Ausführung wirksam und dürfen die tatsächlich freigegebene Leistung bis auf null begrenzen. Die zuvor dokumentierte Ausnahme für einen weggefallenen PV-Überschuss bleibt erhalten. Manuelle Ladungen verwenden weiterhin ihren eigenen Ablauf.

### Gemeinsame Ersparnisbilanz beider Speicher

`/api/savings` summiert die erfassten Tagesbilanzen von Victron und E3/DC. `batteries` liefert die Einzelbilanzen mit erfassten Messstunden und erstem/letztem erfassten Datum. Ohne auswertbare Daten bleibt die Einzelbilanz `null`; tatsächlich gemessene Ruhe wird als Nullbilanz mit Messdauer gespeichert. Tageszeilen enthalten `batteryId`. Der Saldo bleibt eine Periodenbilanz: vermiedene Bezugskosten minus Netzladekosten und entgangene Einspeisevergütung für PV-Ladung, keine bestandsbezogene Gewinnrechnung.

Bei vorhandenen Messungen beider Akkus wird jeder Messabschnitt gemeinsam aufgeteilt: E3/DC-PV deckt zunächst seine direkte Ladung; Übertragungen zwischen den Speichern werden auf Lade- und Entladeseite abgezogen. Verbleibender Netzbezug wird maximal einmal als Netzladung zugeordnet, Netzeinspeisung maximal einmal von der wirksamen Entladung abgezogen. Die Zuordnung bei gleichzeitigem Laden erfolgt anteilig. Ausgewiesen wird die für den Hausverbrauch wirksame Entladung, nicht Export oder Akku-zu-Akku-Transfer. Die Zuordnung ist eine Bilanz aus gemessenen Leistungen, keine separate physische Messung jeder Energiequelle.

Erforderlich sind gültige Leistungsmessungen, bekannte Netz- und E3/DC-PV-Leistung, veröffentlichte Preise und Messabstände bis fünf Minuten. Richtungswechsel zwischen zwei Messpunkten werden für die gemeinsame Bilanz ausgelassen. Fehlende E3/DC-Messungen werden nicht erfunden; die vorhandene Victron-Berechnung bleibt dann nutzbar. Daher können die erfassten Zeiträume unterschiedlich sein. Die Anzeige nennt dies ausdrücklich; insbesondere alte E3/DC-Werte lassen sich nur aus noch vorhandenen Rohmessungen und Preisen nachberechnen. Eine reine Gesamtabfrage aktualisiert keine historischen Rohdaten; Tages-/Wochen-/Monats-/Jahresabfragen aktualisieren ihren gewählten Zeitraum, der Hintergrunddienst die letzten zwei Tage.

Die Speicherung erfolgt nach Speicher, Datum und Währung in `BatterySavingsByBattery`. Bestehende Werte aus `BatterySavingsDailySummaries` werden idempotent als Victron-Werte übernommen; die alte Tabelle bleibt für die Übernahme unverändert erhalten. Wiederholte Berechnung ersetzt die jeweilige Tagesbilanz und addiert sie nicht erneut. Historische Tageswerte ohne rekonstruierbare Zeitabdeckung sind als solche kenntlich. Speichersteuerung und Planung werden durch die Bilanzierung nicht verändert.
