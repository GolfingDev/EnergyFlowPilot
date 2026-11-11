# Persistence Model

## Storage Technology

The controller stores runtime data in SQLite through EF Core.

SQLite is the default production-near storage for the Raspberry Pi deployment.

## Database Creation

On startup, the application must create the database when it does not exist yet.

After creation, the application must seed all known default settings.

If a later application version adds new settings, startup initialization must add the missing defaults without overwriting existing user values.

## Tables

### ControllerSettings

Stores all runtime settings and required access data.

Important columns:

- `Key`
- `Value`
- `Sensitivity`
- `UpdatedAtUtc`

Sensitive settings may be written, but must not be returned to the frontend in plain text.

### DecisionLogEntries

Stores every realtime Battery Decision Engine decision.

Important columns:

- `DecidedAtUtc`
- `ValidFromUtc`
- `ValidToUtc`
- `DecisionState`
- `ChargeSource`
- `TargetPowerWatts`
- `StateOfChargePercent`
- `TibberPricePerKwh`
- `TibberPriceCurrency`
- `GridImportWatts`
- `GridExportWatts`
- `InputSummaryJson`

The input summary must contain enough context to explain why the decision was made, without storing secrets.

The decision log also records the retail price that applied (`TibberPricePerKwh` holds the price of the selected
supplier) and grid power. While joint control is active, the execution writes an entry about every 30 s and only in
status `Active`. Price readers (state 2026-11-11):

| Reader | Price source |
|---|---|
| Historical price query `EfDecisionLogRepository.GetHistoricalPricesAsync` (joint residual energy value, car cheap-price threshold) | `PublishedPriceSlots` first, decision log for quarter hours not archived |
| Energy history (`EfEnergyHistoryRepositories`, energy costs) | decision log only |
| Comparable-day estimate (`ComparableDayPriceEstimator`, single-battery path) | decision log only (`GetDecisionsAsync`) |
| Price course (`PriceTimelineService`) | decision log only |

`decisionLog.retentionDays` (default 90) is not applied yet: no cleanup runs while the last three readers still depend
on old decision log entries (audit 2026-11-11, step D8: switch them to `PublishedPriceSlots`, then apply the retention).

### PublishedPriceSlots

Published retail prices per supplier (`Source`: `tibber`, `octopusGermany`, `energyCharts`), currency and UTC quarter
hour; never estimated prices. `PublishedPriceArchiveBackgroundService` records the selected supplier's prices of the
last 12 and next 48 hours every 15 minutes (a later recording of the same slot replaces the earlier one, `Origin =
provider`). Once, it imports the prices logged with decisions since the oldest decision log entry, one UTC day at a
time with 5 s pauses (`Origin = decisionLog`, same rules as the historical price query; never overrides a recorded
supplier price). Progress: `energyHistory.priceArchiveBackfilledThroughUtc`. Energy-Charts prices are stored as
calculated with the tariff surcharge in force when they were recorded.

Important columns: `Source`, `Currency`, `SlotStartUtc` (key), `SlotEndUtc`, `PricePerKwh` (REAL), `RecordedAtUtc`,
`Origin`. No retention: about 35,000 rows per year.

### DecisionLogReasons

Stores structured reasons for a Battery Decision Engine decision.

Important columns:

- `DecisionLogEntryId`
- `RuleName`
- `Message`

Reasons are deleted together with their decision log entry.

### OperationalEvents

Stores diagnostic events that matter for operation.

Important columns:

- `OccurredAtUtc`
- `Category`
- `Severity`
- `Message`
- `Details`

Operational events are used for visible, diagnosable failures instead of silent fallbacks.

### BatterySavingsByBattery

Stores daily monetary accounting for battery charging and discharging, one row per battery (`victron`, `e3dc`), date
and currency. In EF the entity is exposed as `DbSet BatterySavingsDailySummaries` but mapped to this table. The older
table `BatterySavingsDailySummaries` (Victron only, without `BatteryId`) is still created and only serves the one-time,
idempotent import of old rows as `victron` (`ControllerDbInitializer.EnsurePerBatterySavingsAsync`).

Important columns:

- `BatteryId`
- `MeasuredSeconds` (seconds of the day covered by measurements; 0 for imported rows without known coverage)
- `AccountingDate`
- `Currency`
- `GridChargedEnergyKwh`
- `GridChargeCost`
- `PvChargedEnergyKwh`
- `PvOpportunityCost`
- `DischargedEnergyKwh`
- `DischargeAvoidedCost`
- `NetSavings`
- `UpdatedAtUtc`

The accounting date represents the Europe/Berlin reporting day. The source timestamps remain UTC.

Week, month, year and total reports are calculated by summing daily rows. Weighted average prices are derived from summed energy and cost values.

The first reporting API exposes these views through `GET /api/savings` with `period=day|week|month|year|total`. For bounded periods, the caller must provide `referenceDate=yyyy-MM-dd`.

### HomematicClimateSamples

Stores read-only Homematic room climate readings, one row per supported channel and polling cycle. See `homematic-telemetry.md`.

Important columns:

- `PolledAtUtc` (time axis for analyses)
- `LastChangedAtUtc` (CCU datapoint timestamp, only changes when the value changes)
- `ChannelAddress` (stable hardware address, survives renames)
- `Kind`
- `ActualTemperatureCelsius`, `SetPointTemperatureCelsius`, `SetPointMode`
- `ValveOpeningPercent`, `RelativeHumidityPercent`, `IsWindowOpen`, `IsReachable`

Rows older than `homematic.sampleRetentionDays` are deleted once per day.

## Time Storage

Domain code uses `DateTimeOffset` with UTC timestamps.

SQLite stores these timestamps as Unix milliseconds, so filtering, sorting and indexes work reliably with EF Core.

All cleanup and retention logic must compare UTC timestamps.

## Forecast Input Settings

Until real Victron telemetry and historical consumption persistence are connected, forecast input providers use explicit persisted settings:

- `battery.temporaryStateOfChargePercent`
- `consumptionForecast.averageDailyConsumptionKwh`
- `consumptionForecast.timeZone`

These settings are visible and editable through the same settings API as the rest of the controller configuration.

The temporary state of charge is not a silent fallback. It is an explicit configured input and must be replaced by real Victron telemetry in a later step.
