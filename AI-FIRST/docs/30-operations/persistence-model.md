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

## Time Storage

Domain code uses `DateTimeOffset` with UTC timestamps.

SQLite stores these timestamps as Unix milliseconds, so filtering, sorting and indexes work reliably with EF Core.

All cleanup and retention logic must compare UTC timestamps.
