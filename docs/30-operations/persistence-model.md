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

The decision log is also the only durable record of the retail price that applied (`TibberPricePerKwh` holds the price
of the selected supplier) and a source of grid power. It is read by the energy history (`EfEnergyHistoryRepositories`),
the historical price query (`EfDecisionLogRepository.GetHistoricalPricesAsync`, used by the comparable-day estimate,
the joint residual energy value and the car's cheap-price threshold) and the price course (`PriceTimelineService`).
While joint control is active, the execution writes an entry about every 30 s and only in status `Active`.
`decisionLog.retentionDays` (default 90) is not applied yet: no cleanup runs, because deleting entries would also delete
this price history.

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
