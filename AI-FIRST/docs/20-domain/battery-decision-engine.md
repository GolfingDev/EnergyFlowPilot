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

Forecast charging decisions must consider how long the battery needs to charge:

- required charge energy is calculated from current or projected state of charge and configured total battery capacity
- required charge duration is calculated from required charge energy and configured maximum charge power
- planned grid charging must use the cheapest available 15-minute Tibber price slots needed to reach the target state of charge
- the cheapest slots do not have to be contiguous because the best prices may be scattered across the forecast window
- if multiple slots have the same price, earlier slots should be preferred so the forecast remains deterministic
- if the available cheap or negative slots do not provide enough energy to reach the target state of charge, the Battery Decision Engine must clearly explain the remaining gap
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

Every direct decision must be logged with its input summary, result, target power, relevant time window and structured reasons. Decision log retention must be configurable through persisted settings and frontend-editable later.

Grid feed-in from the battery is forbidden under all circumstances. For `Discharge`, the target power must be limited by the currently measured grid import at the grid connection point. If current grid import is `0` watts or negative, the direct decision must not discharge and must explain that discharging would risk grid feed-in.

Negative grid import means the site is currently exporting energy. The Battery Decision Engine should absorb this export by charging the battery while the battery is not full. The target charge power must be limited by the absolute current grid export and by the configured maximum charge power.

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

- charge the battery during cheap Tibber price phases
- discharge the battery during expensive Tibber price phases
- stay idle during neutral phases
- always use negative Tibber price phases for grid charging when the battery is not full

The current or forecasted battery state of charge must influence the price strategy:

- an empty battery cannot be discharged
- a full battery cannot be charged
- a negative Tibber price overrides normal state-of-charge restrictiveness, except when the battery is already full
- the total usable battery capacity in kWh must come from persisted controller configuration
- the configurable target end state of charge defines an explainable reserve for the end of the planning horizon
- the optional planning minimum state of charge is a softer forecast boundary above the absolute battery protection limit
- if no planning minimum state of charge is configured, the absolute minimum state of charge is used as fallback
- the maximum charge power must come from persisted controller configuration and limits how quickly cheap or negative price windows can fill the battery
- the maximum discharge power must come from persisted controller configuration and limits how quickly expensive price windows can offset grid consumption
- discharge planning must never exceed current or forecasted grid import, because battery feed-in into the grid is forbidden
- negative grid import should lead to charging from surplus while the battery is not full
- preserving headroom for future negative-price grid charging is allowed only when the negative-price value is higher than the configured feed-in compensation
- before a future negative Tibber price window, the Battery Decision Engine may discharge energy above minimum state of charge to avoid normal or expensive grid import and create charging headroom
- if a negative Tibber price slot is reached and the battery still has capacity, the Battery Decision Engine must charge from the grid unless a later negative slot is strictly cheaper
- if configured target end reserve prevents further discharge, the Decision Engine must use an explicit reserve rule and reason
- Golden Scenario audits must report final state of charge and verify it remains at or above target end state of charge
- Golden Scenario audits should use a planning reserve above the hard minimum so the forecast does not optimize exactly to the hardware protection boundary
- a low battery state of charge makes charging easier, so neutral Tibber prices may still lead to charging
- a high battery state of charge makes grid charging more restrictive, so only the cheapest Tibber prices should lead to charging
- every state-of-charge override must be included in the structured reasons

The total battery capacity must not be hardcoded. It must be configurable through the frontend in a future step, transported through DTOs at the API boundary and persisted in the database.

## PV Surplus Priority

PV surplus has priority over the Tibber price strategy for both forecast entries and the current direct decision.

For a given 15-minute slot:

- if expected PV yield is greater than expected consumption, the Battery Decision Engine must choose `Charge` with charge source `PV`
- this rule applies even when Tibber prices would otherwise suggest `Discharge` or `Idle`
- if the battery is already full, the Battery Decision Engine must choose `Idle` with a clear reason because the battery cannot be charged further
- every PV surplus override must be included in the structured reasons

The first implementation step may classify Tibber prices into cheap, neutral and expensive phases based on the relative price distribution inside the available forecast window:

- cheap phase: lower price range, suitable for grid charging
- neutral phase: middle price range, suitable for idle
- expensive phase: upper price range, suitable for discharging

Later optimization must include battery capacity, minimum state of charge, maximum charge/discharge power, efficiency losses, expected consumption and expected PV yield. Those parameters are required before the strategy can be considered a true cost optimizer.

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
